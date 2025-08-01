// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XX                                                                           XX
XX                             emitriscv64.cpp                               XX
XX                                                                           XX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
*/

#include "jitpch.h"
#ifdef _MSC_VER
#pragma hdrstop
#endif

#if defined(TARGET_RISCV64)

/*****************************************************************************/
/*****************************************************************************/

#include "instr.h"
#include "emit.h"
#include "codegen.h"

/*****************************************************************************/

const instruction emitJumpKindInstructions[] = {
    INS_nop,

#define JMP_SMALL(en, rev, ins) INS_##ins,
#include "emitjmps.h"
};

const emitJumpKind emitReverseJumpKinds[] = {
    EJ_NONE,

#define JMP_SMALL(en, rev, ins) EJ_##rev,
#include "emitjmps.h"
};

/*****************************************************************************
 * Look up the instruction for a jump kind
 */

/*static*/ instruction emitter::emitJumpKindToIns(emitJumpKind jumpKind)
{
    assert((unsigned)jumpKind < ArrLen(emitJumpKindInstructions));
    return emitJumpKindInstructions[jumpKind];
}

/*****************************************************************************
 * Reverse the conditional jump
 */

/*static*/ emitJumpKind emitter::emitReverseJumpKind(emitJumpKind jumpKind)
{
    assert(jumpKind < EJ_COUNT);
    return emitReverseJumpKinds[jumpKind];
}

/*****************************************************************************
 *
 *  Return the allocated size (in bytes) of the given instruction descriptor.
 */

size_t emitter::emitSizeOfInsDsc(instrDesc* id) const
{
    if (emitIsSmallInsDsc(id))
        return SMALL_IDSC_SIZE;

    insOpts insOp = id->idInsOpt();

    switch (insOp)
    {
        case INS_OPTS_JALR:
        case INS_OPTS_J_cond:
        case INS_OPTS_J:
            return sizeof(instrDescJmp);

        case INS_OPTS_C:
            if (id->idIsLargeCall())
            {
                /* Must be a "fat" call descriptor */
                return sizeof(instrDescCGCA);
            }
            else
            {
                assert(!id->idIsLargeDsp());
                assert(!id->idIsLargeCns());
                return sizeof(instrDesc);
            }

        case INS_OPTS_RC:
        case INS_OPTS_RL:
        case INS_OPTS_RELOC:
        case INS_OPTS_NONE:
            return sizeof(instrDesc);
        case INS_OPTS_I:
            return sizeof(instrDescLoadImm);
        default:
            NO_WAY("unexpected instruction descriptor format");
            break;
    }
}

bool emitter::emitInsWritesToLclVarStackLoc(instrDesc* id)
{
    if (!id->idIsLclVar())
        return false;

    instruction ins = id->idIns();

    // This list is related to the list of instructions used to store local vars in emitIns_S_R().
    // We don't accept writing to float local vars.

    switch (ins)
    {
        case INS_sd:
        case INS_sw:
        case INS_sb:
        case INS_sh:
            return true;

        default:
            return false;
    }
}

#define LD 1
#define ST 2

// clang-format off
/*static*/ const BYTE CodeGenInterface::instInfo[] =
{
    #define INST(id, nm, info, e1) info,
    #include "instrs.h"
};
// clang-format on

emitter::MajorOpcode emitter::GetMajorOpcode(code_t code)
{
    assert((code & 0b11) == 0b11); // 16-bit instructions unsupported
    code_t opcode = (code >> 2) & 0b11111;
    assert((opcode & 0b111) != 0b111); // 48-bit and larger instructions unsupported
    return (MajorOpcode)opcode;
}

inline bool emitter::emitInsMayWriteToGCReg(instruction ins)
{
    assert(ins != INS_invalid);
    if (ins == INS_nop || ins == INS_j) // pseudos with 'zero' destination register
        return false;

    if (ins == INS_lea)
        return true;

    code_t code = emitInsCode(ins);
    switch (GetMajorOpcode(code))
    {
        // Opcodes with no destination register
        case MajorOpcode::Store:
        case MajorOpcode::StoreFp:
        case MajorOpcode::MiscMem:
        case MajorOpcode::Branch:
        // Opcodes with a floating-point destination register
        case MajorOpcode::LoadFp:
        case MajorOpcode::MAdd:
        case MajorOpcode::MSub:
        case MajorOpcode::NmSub:
        case MajorOpcode::NmAdd:
            return false;

        case MajorOpcode::System:
        {
            code_t funct3 = (code >> 12) & 0b111;
            return (funct3 != 0); // CSR read/writes
        }

        case MajorOpcode::OpFp:
        {
            // Lowest 2 bits of funct7 distinguish single, double, half, and quad floats; we don't care
            code_t funct7 = code >> (25 + 2);
            switch (funct7)
            {
                case 0b10100: // comparisons: feq, flt, fle
                case 0b11100: // fmv to integer or fclass
                case 0b11000: // fcvt to integer
                    return true;
                default:
                    return false;
            }
        }

        case MajorOpcode::Custom0:
        case MajorOpcode::Custom1:
        case MajorOpcode::Custom2Rv128:
        case MajorOpcode::Custom3Rv128:
        case MajorOpcode::OpV:
        case MajorOpcode::OpVe:
        case MajorOpcode::Reserved:
            assert(!"unsupported major opcode");
            FALLTHROUGH;

        default: // all other opcodes write to a general purpose destination register
            return true;
    }
}

//------------------------------------------------------------------------
// emitInsLoad: Returns true if the instruction is some kind of load instruction.
//
bool emitter::emitInsIsLoad(instruction ins)
{
    // We have pseudo ins like lea which are not included in emitInsLdStTab.
    if (ins < ArrLen(CodeGenInterface::instInfo))
        return (CodeGenInterface::instInfo[ins] & LD) != 0;
    else
        return false;
}

//------------------------------------------------------------------------
// emitInsIsStore: Returns true if the instruction is some kind of store instruction.
//
bool emitter::emitInsIsStore(instruction ins)
{
    // We have pseudo ins like lea which are not included in emitInsLdStTab.
    if (ins < ArrLen(CodeGenInterface::instInfo))
        return (CodeGenInterface::instInfo[ins] & ST) != 0;
    else
        return false;
}

//-------------------------------------------------------------------------
// emitInsIsLoadOrStore: Returns true if the instruction is some kind of load/store instruction.
//
bool emitter::emitInsIsLoadOrStore(instruction ins)
{
    // We have pseudo ins like lea which are not included in emitInsLdStTab.
    if (ins < ArrLen(CodeGenInterface::instInfo))
        return (CodeGenInterface::instInfo[ins] & (LD | ST)) != 0;
    else
        return false;
}

/*****************************************************************************
 *
 *  Returns the specific encoding of the given CPU instruction.
 */

inline emitter::code_t emitter::emitInsCode(instruction ins /*, insFormat fmt*/)
{
    code_t code = BAD_CODE;

    // clang-format off
    const static code_t insCode[] =
    {
        #define INST(id, nm, info, e1) e1,
        #include "instrs.h"
    };
    // clang-format on

    assert(ins < ArrLen(insCode));
    code = insCode[ins];

    assert(code != BAD_CODE);

    return code;
}

/****************************************************************************
 *
 *  Add an instruction with no operands.
 */

void emitter::emitIns(instruction ins)
{
    instrDesc* id = emitNewInstr(EA_8BYTE);

    id->idIns(ins);
    id->idAddr()->iiaSetInstrEncode(emitInsCode(ins));
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *  emitter::emitIns_S_R(), emitter::emitIns_S_R_R() and emitter::emitIns_R_S():
 *
 *  Add an Load/Store instruction(s): base+offset and base-addr-computing if needed.
 *  For referencing a stack-based local variable and a register
 *
 */
void emitter::emitIns_S_R(instruction ins, emitAttr attr, regNumber reg1, int varx, int offs)
{
    emitIns_S_R_R(ins, attr, reg1, REG_NA, varx, offs);
}

void emitter::emitIns_S_R_R(instruction ins, emitAttr attr, regNumber reg1, regNumber tmpReg, int varx, int offs)
{
    ssize_t imm;

    assert(tmpReg != codeGen->rsGetRsvdReg());
    assert(reg1 != codeGen->rsGetRsvdReg());

    emitAttr size = EA_SIZE(attr);

#ifdef DEBUG
    switch (ins)
    {
        case INS_sd:
        case INS_sw:
        case INS_sh:
        case INS_sb:
        case INS_fsd:
        case INS_fsw:
            break;

        default:
            NYI_RISCV64("illegal ins within emitIns_S_R_R!");
            return;

    } // end switch (ins)
#endif

    /* Figure out the variable's frame position */
    int  base;
    bool FPbased;

    base = emitComp->lvaFrameAddress(varx, &FPbased);

    regNumber regBase = FPbased ? REG_FPBASE : REG_SPBASE;
    regNumber reg2;

    if (tmpReg == REG_NA)
    {
        reg2 = regBase;
        imm  = base + offs;
    }
    else
    {
        reg2 = tmpReg;
        imm  = offs;
    }

    assert(reg2 != REG_NA && reg2 != codeGen->rsGetRsvdReg());

    if (!isValidSimm12(imm))
    {
        // If immediate does not fit to store immediate 12 bits, construct necessary value in rsRsvdReg()
        // and keep tmpReg hint value unchanged.
        assert(isValidSimm20((imm + 0x800) >> 12));

        emitIns_R_I(INS_lui, EA_PTRSIZE, codeGen->rsGetRsvdReg(), (imm + 0x800) >> 12);
        emitIns_R_R_R(INS_add, EA_PTRSIZE, codeGen->rsGetRsvdReg(), codeGen->rsGetRsvdReg(), reg2);

        imm  = imm & 0xfff;
        reg2 = codeGen->rsGetRsvdReg();
    }

    instrDesc* id = emitNewInstr(attr);

    id->idReg1(reg1);

    id->idReg2(reg2);

    id->idIns(ins);

    assert(isGeneralRegister(reg2));
    code_t code = emitInsCode(ins);
    code |= (code_t)(reg1 & 0x1f) << 20;
    code |= (code_t)reg2 << 15;
    code |= (((imm >> 5) & 0x7f) << 25) | ((imm & 0x1f) << 7);

    id->idAddr()->iiaSetInstrEncode(code);
    id->idAddr()->iiaLclVar.initLclVarAddr(varx, offs);
    id->idSetIsLclVar();
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*
 *  Special notes for `offs`, please see the comment for `emitter::emitIns_S_R`.
 */
void emitter::emitIns_R_S(instruction ins, emitAttr attr, regNumber reg1, int varx, int offs)
{
    ssize_t imm;

    emitAttr size = EA_SIZE(attr);

#ifdef DEBUG
    switch (ins)
    {
        case INS_lb:
        case INS_lbu:

        case INS_lh:
        case INS_lhu:

        case INS_lw:
        case INS_lwu:
        case INS_flw:

        case INS_ld:
        case INS_fld:

            break;

        case INS_lea:
            assert(size == EA_8BYTE);
            break;

        default:
            NYI_RISCV64("illegal ins within emitIns_R_S!");
            return;

    } // end switch (ins)
#endif

    /* Figure out the variable's frame position */
    int  base;
    bool FPbased;

    base = emitComp->lvaFrameAddress(varx, &FPbased);
    imm  = offs < 0 ? -offs - 8 : base + offs;

    regNumber reg2 = FPbased ? REG_FPBASE : REG_SPBASE;
    assert(offs >= 0);
    offs = offs < 0 ? -offs - 8 : offs;

    reg1 = (regNumber)((char)reg1 & 0x1f);
    code_t code;
    if ((-2048 <= imm) && (imm < 2048))
    {
        if (ins == INS_lea)
        {
            ins = INS_addi;
        }
        code = emitInsCode(ins);
        code |= (code_t)reg1 << 7;
        code |= (code_t)reg2 << 15;
        code |= (imm & 0xfff) << 20;
    }
    else
    {
        if (ins == INS_lea)
        {
            assert(isValidSimm20((imm + 0x800) >> 12));
            emitIns_R_I(INS_lui, EA_PTRSIZE, codeGen->rsGetRsvdReg(), (imm + 0x800) >> 12);
            ssize_t imm2 = imm & 0xfff;
            emitIns_R_R_I(INS_addi, EA_PTRSIZE, codeGen->rsGetRsvdReg(), codeGen->rsGetRsvdReg(), imm2);

            ins  = INS_add;
            code = emitInsCode(ins);
            code |= (code_t)reg1 << 7;
            code |= (code_t)reg2 << 15;
            code |= (code_t)codeGen->rsGetRsvdReg() << 20;
        }
        else
        {
            assert(isValidSimm20((imm + 0x800) >> 12));
            emitIns_R_I(INS_lui, EA_PTRSIZE, codeGen->rsGetRsvdReg(), (imm + 0x800) >> 12);

            emitIns_R_R_R(INS_add, EA_PTRSIZE, codeGen->rsGetRsvdReg(), codeGen->rsGetRsvdReg(), reg2);

            ssize_t imm2 = imm & 0xfff;
            code         = emitInsCode(ins);
            code |= (code_t)reg1 << 7;
            code |= (code_t)codeGen->rsGetRsvdReg() << 15;
            code |= (code_t)(imm2 & 0xfff) << 20;
        }
    }

    instrDesc* id = emitNewInstr(attr);

    id->idReg1(reg1);

    id->idIns(ins);

    id->idAddr()->iiaSetInstrEncode(code);
    id->idAddr()->iiaLclVar.initLclVarAddr(varx, offs);
    id->idSetIsLclVar();
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Add an instruction with a single immediate value.
 */

void emitter::emitIns_I(instruction ins, emitAttr attr, ssize_t imm)
{
    code_t code = emitInsCode(ins);

    switch (ins)
    {
        case INS_fence:
            code |= ((imm & 0xff) << 20);
            break;
        case INS_j:
            assert(imm >= -1048576 && imm < 1048576);
            code |= ((imm >> 12) & 0xff) << 12;
            code |= ((imm >> 11) & 0x1) << 20;
            code |= ((imm >> 1) & 0x3ff) << 21;
            code |= ((imm >> 20) & 0x1) << 31;
            break;
        default:
            NO_WAY("illegal ins within emitIns_I!");
    }

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

void emitter::emitIns_I_I(instruction ins, emitAttr attr, ssize_t cc, ssize_t offs)
{
    NYI_RISCV64("emitIns_I_I-----unimplemented/unused on RISCV64 yet----");
}

/*****************************************************************************
 *
 *  Add an instruction referencing a register and a constant.
 */

void emitter::emitIns_R_I(instruction ins, emitAttr attr, regNumber reg, ssize_t imm, insOpts opt /* = INS_OPTS_NONE */)
{
    code_t code = emitInsCode(ins);

    switch (ins)
    {
        case INS_lui:
        case INS_auipc:
            assert(reg != REG_R0);
            assert(isGeneralRegister(reg));
            assert(isValidSimm20(imm));

            code |= reg << 7;
            code |= (imm & 0xfffff) << 12;
            break;
        case INS_jal:
            assert(isGeneralRegisterOrR0(reg));
            assert(isValidSimm21(imm));

            code |= reg << 7;
            code |= ((imm >> 12) & 0xff) << 12;
            code |= ((imm >> 11) & 0x1) << 20;
            code |= ((imm >> 1) & 0x3ff) << 21;
            code |= ((imm >> 20) & 0x1) << 31;
            break;
        default:
            NO_WAY("illegal ins within emitIns_R_I!");
            break;
    } // end switch (ins)

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idReg1(reg);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

//------------------------------------------------------------------------
// emitIns_Mov: Emits a move instruction
//
// Arguments:
//    ins       -- The instruction being emitted
//    attr      -- The emit attribute
//    dstReg    -- The destination register
//    srcReg    -- The source register
//    canSkip   -- true if the move can be elided when dstReg == srcReg, otherwise false
//    insOpts   -- The instruction options
//
void emitter::emitIns_Mov(
    instruction ins, emitAttr attr, regNumber dstReg, regNumber srcReg, bool canSkip, insOpts opt /* = INS_OPTS_NONE */)
{
    if (!canSkip || (dstReg != srcReg))
    {
        if ((EA_4BYTE == attr) && (INS_mov == ins))
        {
            assert(isGeneralRegisterOrR0(srcReg));
            assert(isGeneralRegisterOrR0(dstReg));
            emitIns_R_R_I(INS_addiw, attr, dstReg, srcReg, 0);
        }
        else if (INS_fsgnj_s == ins || INS_fsgnj_d == ins)
        {
            assert(isFloatReg(srcReg));
            assert(isFloatReg(dstReg));
            emitIns_R_R_R(ins, attr, dstReg, srcReg, srcReg);
        }
        else if (genIsValidFloatReg(srcReg) || genIsValidFloatReg(dstReg))
        {
            emitIns_R_R(ins, attr, dstReg, srcReg);
        }
        else
        {
            assert(isGeneralRegisterOrR0(srcReg));
            assert(isGeneralRegisterOrR0(dstReg));
            emitIns_R_R_I(INS_addi, attr, dstReg, srcReg, 0);
        }
    }
}

void emitter::emitIns_Mov(emitAttr attr, regNumber dstReg, regNumber srcReg, bool canSkip)
{
    if (!canSkip || dstReg != srcReg)
    {
        assert(attr == EA_4BYTE || attr == EA_PTRSIZE);
        if (isGeneralRegisterOrR0(dstReg) && isGeneralRegisterOrR0(srcReg))
        {
            emitIns_R_R_I(attr == EA_4BYTE ? INS_addiw : INS_addi, attr, dstReg, srcReg, 0);
        }
        else if (isGeneralRegisterOrR0(dstReg) && genIsValidFloatReg(srcReg))
        {
            emitIns_R_R(attr == EA_4BYTE ? INS_fmv_x_w : INS_fmv_x_d, attr, dstReg, srcReg);
        }
        else if (genIsValidFloatReg(dstReg) && isGeneralRegisterOrR0(srcReg))
        {
            emitIns_R_R(attr == EA_4BYTE ? INS_fmv_w_x : INS_fmv_d_x, attr, dstReg, srcReg);
        }
        else if (genIsValidFloatReg(dstReg) && genIsValidFloatReg(srcReg))
        {
            emitIns_R_R_R(attr == EA_4BYTE ? INS_fsgnj_s : INS_fsgnj_d, attr, dstReg, srcReg, srcReg);
        }
        else
        {
            assert(!"Invalid registers in emitIns_Mov()\n");
        }
    }
}

/*****************************************************************************
 *
 *  Add an instruction referencing two registers
 */

void emitter::emitIns_R_R(
    instruction ins, emitAttr attr, regNumber reg1, regNumber reg2, insOpts opt /* = INS_OPTS_NONE */)
{
    code_t code = emitInsCode(ins);

    if (INS_mov == ins || INS_sext_w == ins || (INS_clz <= ins && ins <= INS_rev8))
    {
        assert(isGeneralRegisterOrR0(reg1));
        assert(isGeneralRegisterOrR0(reg2));
        code |= reg1 << 7;
        code |= reg2 << 15;
    }
    else if (INS_fmv_x_d == ins || INS_fmv_x_w == ins || INS_fclass_s == ins || INS_fclass_d == ins)
    {
        assert(isGeneralRegisterOrR0(reg1));
        assert(isFloatReg(reg2));
        code |= reg1 << 7;
        code |= (reg2 & 0x1f) << 15;
    }
    else if (INS_fcvt_w_s == ins || INS_fcvt_wu_s == ins || INS_fcvt_w_d == ins || INS_fcvt_wu_d == ins ||
             INS_fcvt_l_s == ins || INS_fcvt_lu_s == ins || INS_fcvt_l_d == ins || INS_fcvt_lu_d == ins)
    {
        assert(isGeneralRegisterOrR0(reg1));
        assert(isFloatReg(reg2));
        code |= reg1 << 7;
        code |= (reg2 & 0x1f) << 15;
        code |= 0x1 << 12;
    }
    else if (INS_fmv_w_x == ins || INS_fmv_d_x == ins)
    {
        assert(isFloatReg(reg1));
        assert(isGeneralRegisterOrR0(reg2));
        code |= (reg1 & 0x1f) << 7;
        code |= reg2 << 15;
    }
    else if (INS_fcvt_s_w == ins || INS_fcvt_s_wu == ins || INS_fcvt_d_w == ins || INS_fcvt_d_wu == ins ||
             INS_fcvt_s_l == ins || INS_fcvt_s_lu == ins || INS_fcvt_d_l == ins || INS_fcvt_d_lu == ins)
    {
        assert(isFloatReg(reg1));
        assert(isGeneralRegisterOrR0(reg2));
        code |= (reg1 & 0x1f) << 7;
        code |= reg2 << 15;
        if (INS_fcvt_d_w != ins && INS_fcvt_d_wu != ins) // fcvt.d.w[u] always produces an exact result
            code |= 0x7 << 12;                           // round according to frm status register
    }
    else if (INS_fcvt_s_d == ins || INS_fcvt_d_s == ins || INS_fsqrt_s == ins || INS_fsqrt_d == ins)
    {
        assert(isFloatReg(reg1));
        assert(isFloatReg(reg2));
        code |= (reg1 & 0x1f) << 7;
        code |= (reg2 & 0x1f) << 15;
        if (INS_fcvt_d_s != ins) // fcvt.d.s never rounds
            code |= 0x7 << 12;   // round according to frm status register
    }
    else
    {
        NYI_RISCV64("illegal ins within emitIns_R_R!");
    }

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idReg1(reg1);
    id->idReg2(reg2);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Add an instruction referencing two registers and a constant.
 */

void emitter::emitIns_R_R_I(
    instruction ins, emitAttr attr, regNumber reg1, regNumber reg2, ssize_t imm, insOpts opt /* = INS_OPTS_NONE */)
{
    code_t     code = emitInsCode(ins);
    instrDesc* id   = emitNewInstr(attr);

    if ((INS_addi <= ins && INS_srai >= ins) || (INS_addiw <= ins && INS_sraiw >= ins) ||
        (INS_lb <= ins && INS_lhu >= ins) || INS_ld == ins || INS_lw == ins || INS_jalr == ins || INS_fld == ins ||
        INS_flw == ins || INS_slli_uw == ins || INS_rori == ins || INS_roriw == ins)
    {
        assert(isGeneralRegister(reg2));
        code |= (reg1 & 0x1f) << 7; // rd
        code |= reg2 << 15;         // rs1
        code |= imm << 20;          // imm
    }
    else if (INS_sd == ins || INS_sw == ins || INS_sh == ins || INS_sb == ins || INS_fsw == ins || INS_fsd == ins)
    {
        assert(isGeneralRegister(reg2));
        code |= (reg1 & 0x1f) << 20;                               // rs2
        code |= reg2 << 15;                                        // rs1
        code |= (((imm >> 5) & 0x7f) << 25) | ((imm & 0x1f) << 7); // imm
    }
    else if (INS_beq <= ins && INS_bgeu >= ins)
    {
        assert(isGeneralRegister(reg1));
        assert(isGeneralRegister(reg2));
        assert(isValidSimm13(imm));
        assert(!(imm & 3));
        code |= reg1 << 15;
        code |= reg2 << 20;
        code |= ((imm >> 11) & 0x1) << 7;
        code |= ((imm >> 1) & 0xf) << 8;
        code |= ((imm >> 5) & 0x3f) << 25;
        code |= ((imm >> 12) & 0x1) << 31;
        // TODO-RISCV64: Move jump logic to emitIns_J
        id->idAddr()->iiaSetInstrCount(static_cast<int>(imm / sizeof(code_t)));
    }
    else if (ins == INS_csrrs || ins == INS_csrrw || ins == INS_csrrc)
    {
        assert(isGeneralRegisterOrR0(reg1));
        assert(isGeneralRegisterOrR0(reg2));
        assert(isValidUimm12(imm));
        code |= reg1 << 7;
        code |= reg2 << 15;
        code |= imm << 20;
    }
    else
    {
        NYI_RISCV64("illegal ins within emitIns_R_R_I!");
    }

    id->idIns(ins);
    id->idReg1(reg1);
    id->idReg2(reg2);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Add an instruction referencing register and two constants.
 */

void emitter::emitIns_R_I_I(
    instruction ins, emitAttr attr, regNumber reg1, ssize_t imm1, ssize_t imm2, insOpts opt) /* = INS_OPTS_NONE */
{
    code_t code = emitInsCode(ins);

    if (INS_csrrwi <= ins && ins <= INS_csrrci)
    {
        assert(isGeneralRegisterOrR0(reg1));
        assert(isValidUimm5(imm1));
        assert(isValidUimm12(imm2));
        code |= reg1 << 7;
        code |= imm1 << 15;
        code |= imm2 << 20;
    }
    else
    {
        NYI_RISCV64("illegal ins within emitIns_R_I_I!");
    }
    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idReg1(reg1);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Add an instruction referencing three registers.
 */

void emitter::emitIns_R_R_R(
    instruction ins, emitAttr attr, regNumber reg1, regNumber reg2, regNumber reg3, insOpts opt) /* = INS_OPTS_NONE */
{
    code_t code = emitInsCode(ins);

    if ((INS_add <= ins && ins <= INS_and) || (INS_mul <= ins && ins <= INS_remuw) ||
        (INS_addw <= ins && ins <= INS_sraw) || (INS_fadd_s <= ins && ins <= INS_fmax_s) ||
        (INS_fadd_d <= ins && ins <= INS_fmax_d) || (INS_feq_s <= ins && ins <= INS_fle_s) ||
        (INS_feq_d <= ins && ins <= INS_fle_d) || (INS_lr_w <= ins && ins <= INS_amomaxu_d) ||
        (INS_sh1add <= ins && ins <= INS_sh3add_uw) || (INS_rol <= ins && ins <= INS_maxu))
    {
#ifdef DEBUG
        switch (ins)
        {
            case INS_add:
            case INS_sub:
            case INS_sll:
            case INS_slt:
            case INS_sltu:
            case INS_xor:
            case INS_srl:
            case INS_sra:
            case INS_or:
            case INS_and:

            case INS_addw:
            case INS_subw:
            case INS_sllw:
            case INS_srlw:
            case INS_sraw:

            case INS_mul:
            case INS_mulh:
            case INS_mulhsu:
            case INS_mulhu:
            case INS_div:
            case INS_divu:
            case INS_rem:
            case INS_remu:

            case INS_mulw:
            case INS_divw:
            case INS_divuw:
            case INS_remw:
            case INS_remuw:

            case INS_fadd_s:
            case INS_fsub_s:
            case INS_fmul_s:
            case INS_fdiv_s:
            case INS_fsgnj_s:
            case INS_fsgnjn_s:
            case INS_fsgnjx_s:
            case INS_fmin_s:
            case INS_fmax_s:

            case INS_feq_s:
            case INS_flt_s:
            case INS_fle_s:

            case INS_fadd_d:
            case INS_fsub_d:
            case INS_fmul_d:
            case INS_fdiv_d:
            case INS_fsgnj_d:
            case INS_fsgnjn_d:
            case INS_fsgnjx_d:
            case INS_fmin_d:
            case INS_fmax_d:

            case INS_feq_d:
            case INS_flt_d:
            case INS_fle_d:

            case INS_lr_w:
            case INS_lr_d:
            case INS_sc_w:
            case INS_sc_d:
            case INS_amoswap_w:
            case INS_amoswap_d:
            case INS_amoadd_w:
            case INS_amoadd_d:
            case INS_amoxor_w:
            case INS_amoxor_d:
            case INS_amoand_w:
            case INS_amoand_d:
            case INS_amoor_w:
            case INS_amoor_d:
            case INS_amomin_w:
            case INS_amomin_d:
            case INS_amomax_w:
            case INS_amomax_d:
            case INS_amominu_w:
            case INS_amominu_d:
            case INS_amomaxu_w:
            case INS_amomaxu_d:

            case INS_sh1add:
            case INS_sh2add:
            case INS_sh3add:
            case INS_add_uw:
            case INS_sh1add_uw:
            case INS_sh2add_uw:
            case INS_sh3add_uw:

            case INS_rol:
            case INS_rolw:
            case INS_ror:
            case INS_rorw:
            case INS_xnor:
            case INS_orn:
            case INS_andn:
            case INS_min:
            case INS_minu:
            case INS_max:
            case INS_maxu:
                break;
            default:
                NYI_RISCV64("illegal ins within emitIns_R_R_R!");
        }

#endif
        // Src/data register for load reserved should be empty
        assert((ins != INS_lr_w && ins != INS_lr_d) || reg3 == REG_R0);

        code |= ((reg1 & 0x1f) << 7);
        code |= ((reg2 & 0x1f) << 15);
        code |= ((reg3 & 0x1f) << 20);
        if ((INS_fadd_s <= ins && INS_fdiv_s >= ins) || (INS_fadd_d <= ins && INS_fdiv_d >= ins))
        {
            code |= 0x7 << 12;
        }
        else if (ins == INS_sc_w || ins == INS_sc_d)
        {
            code |= 0b10 << 25; // release ordering, it ends the lr-sc loop
        }
        else if ((ins == INS_lr_w || ins == INS_lr_d) || (INS_amoswap_w <= ins && ins <= INS_amomaxu_d))
        {
            // For now all atomics are seq. consistent as Interlocked.* APIs don't expose acquire/release ordering
            code |= 0b11 << 25;
        }
    }
    else
    {
        NYI_RISCV64("illegal ins within emitIns_R_R_R!");
    }

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idReg1(reg1);
    id->idReg2(reg2);
    id->idReg3(reg3);
    id->idAddr()->iiaSetInstrEncode(code);
    id->idCodeSize(4);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Add an instruction referencing three registers and a constant.
 */

void emitter::emitIns_R_R_R_I(instruction ins,
                              emitAttr    attr,
                              regNumber   reg1,
                              regNumber   reg2,
                              regNumber   reg3,
                              ssize_t     imm,
                              insOpts     opt /* = INS_OPTS_NONE */,
                              emitAttr    attrReg2 /* = EA_UNKNOWN */)
{
    NYI_RISCV64("emitIns_R_R_R_I-----unimplemented/unused on RISCV64 yet----");
}

/*****************************************************************************
 *
 *  Add an instruction referencing two registers and two constants.
 */

void emitter::emitIns_R_R_I_I(
    instruction ins, emitAttr attr, regNumber reg1, regNumber reg2, int imm1, int imm2, insOpts opt)
{
    NYI_RISCV64("emitIns_R_R_I_I-----unimplemented/unused on RISCV64 yet----");
}

/*****************************************************************************
 *
 *  Add an instruction referencing four registers.
 */

void emitter::emitIns_R_R_R_R(
    instruction ins, emitAttr attr, regNumber reg1, regNumber reg2, regNumber reg3, regNumber reg4)
{
    NYI_RISCV64("emitIns_R_R_R_R-----unimplemented/unused on RISCV64 yet----");
}

/*****************************************************************************
 *
 *  Add an instruction with a register + static member operands.
 *  Constant is stored into JIT data which is adjacent to code.
 *
 */
void emitter::emitIns_R_C(
    instruction ins, emitAttr attr, regNumber destReg, regNumber addrReg, CORINFO_FIELD_HANDLE fldHnd)
{
    instrDesc* id = emitNewInstr(attr);
    id->idIns(ins);
    assert(destReg != REG_R0); // for special. reg Must not be R0.
    id->idReg1(destReg);
    id->idInsOpt(INS_OPTS_RC);
    id->idCodeSize(2 * sizeof(code_t)); // auipc + load/addi

    if (EA_IS_GCREF(attr))
    {
        /* A special value indicates a GCref pointer value */
        id->idGCref(GCT_GCREF);
        id->idOpSize(EA_PTRSIZE);
    }
    else if (EA_IS_BYREF(attr))
    {
        /* A special value indicates a Byref pointer value */
        id->idGCref(GCT_BYREF);
        id->idOpSize(EA_PTRSIZE);
    }

    // TODO-RISCV64: this maybe deleted.
    id->idSetIsBound(); // We won't patch address since we will know the exact distance
                        // once JIT code and data are allocated together.

    assert(addrReg == REG_NA); // NOTE: for RISV64, not support addrReg != REG_NA.

    id->idAddr()->iiaFieldHnd = fldHnd;

    appendToCurIG(id);
}

void emitter::emitIns_R_AR(instruction ins, emitAttr attr, regNumber ireg, regNumber reg, int offs)
{
    NYI_RISCV64("emitIns_R_AR-----unimplemented/unused on RISCV64 yet----");
}

// This computes address from the immediate which is relocatable.
void emitter::emitIns_R_AI(instruction  ins,
                           emitAttr     attr,
                           regNumber    reg,
                           ssize_t addr DEBUGARG(size_t targetHandle) DEBUGARG(GenTreeFlags gtFlags))
{
    assert(EA_IS_RELOC(attr)); // EA_PTR_DSP_RELOC
    assert(ins == INS_jal);    // for special.
    assert(isGeneralRegister(reg));
    // INS_OPTS_RELOC: placeholders.  2-ins:
    //  case:EA_HANDLE_CNS_RELOC
    //   auipc  reg, off-hi-20bits
    //   addi   reg, reg, off-lo-12bits
    //  case:EA_PTR_DSP_RELOC
    //   auipc  reg, off-hi-20bits
    //   ld     reg, reg, off-lo-12bits

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    assert(reg != REG_R0); // for special. reg Must not be R0.
    id->idReg1(reg);       // destination register that will get the constant value.

    id->idInsOpt(INS_OPTS_RELOC);

    if (EA_IS_GCREF(attr))
    {
        /* A special value indicates a GCref pointer value */
        id->idGCref(GCT_GCREF);
        id->idOpSize(EA_PTRSIZE);
    }
    else if (EA_IS_BYREF(attr))
    {
        /* A special value indicates a Byref pointer value */
        id->idGCref(GCT_BYREF);
        id->idOpSize(EA_PTRSIZE);
    }

    id->idAddr()->iiaAddr = (BYTE*)addr;
    id->idCodeSize(8);

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Record that a jump instruction uses the short encoding
 *
 */
void emitter::emitSetShortJump(instrDescJmp* id)
{
    // TODO-RISCV64: maybe delete it on future.
    NYI_RISCV64("emitSetShortJump-----unimplemented/unused on RISCV64 yet----");
}

/*****************************************************************************
 *
 *  Add a label instruction.
 */

void emitter::emitIns_R_L(instruction ins, emitAttr attr, BasicBlock* dst, regNumber reg)
{
    assert(dst->HasFlag(BBF_HAS_LABEL));

    // 2-ins:
    //   auipc reg, offset-hi20
    //   addi  reg, reg, offset-lo12

    instrDesc* id = emitNewInstr(attr);

    id->idIns(ins);
    id->idInsOpt(INS_OPTS_RL);
    id->idAddr()->iiaBBlabel = dst;

    if (emitComp->opts.compReloc)
        id->idSetIsDspReloc();

    id->idCodeSize(2 * sizeof(code_t));
    id->idReg1(reg);

    if (EA_IS_GCREF(attr))
    {
        /* A special value indicates a GCref pointer value */
        id->idGCref(GCT_GCREF);
        id->idOpSize(EA_PTRSIZE);
    }
    else if (EA_IS_BYREF(attr))
    {
        /* A special value indicates a Byref pointer value */
        id->idGCref(GCT_BYREF);
        id->idOpSize(EA_PTRSIZE);
    }

#ifdef DEBUG
    // Mark the catch return
    if (emitComp->compCurBB->KindIs(BBJ_EHCATCHRET))
    {
        id->idDebugOnlyInfo()->idCatchRet = true;
    }
#endif // DEBUG

    appendToCurIG(id);
}

void emitter::emitIns_J_R(instruction ins, emitAttr attr, BasicBlock* dst, regNumber reg)
{
    NYI_RISCV64("emitIns_J_R-----unimplemented/unused on RISCV64 yet----");
}

void emitter::emitIns_J(instruction ins, BasicBlock* dst, int instrCount)
{
    assert(dst != nullptr);
    //
    // INS_OPTS_J: placeholders.  1-ins: if the dst outof-range will be replaced by INS_OPTS_JALR.
    // jal/j/jalr/bnez/beqz/beq/bne/blt/bge/bltu/bgeu dst

    assert(dst->HasFlag(BBF_HAS_LABEL));

    instrDescJmp* id = emitNewInstrJmp();
    assert((INS_jal <= ins) && (ins <= INS_bgeu));
    id->idIns(ins);
    id->idReg1((regNumber)(instrCount & 0x1f));
    id->idReg2((regNumber)((instrCount >> 5) & 0x1f));

    id->idInsOpt(INS_OPTS_J);
    emitCounts_INS_OPTS_J++;
    id->idAddr()->iiaBBlabel = dst;

    if (emitComp->opts.compReloc)
    {
        id->idSetIsDspReloc();
    }

    id->idjShort = false;

    // TODO-RISCV64: maybe deleted this.
    id->idjKeepLong = emitComp->fgInDifferentRegions(emitComp->compCurBB, dst);
#ifdef DEBUG
    if (emitComp->opts.compLongAddress) // Force long branches
        id->idjKeepLong = 1;
#endif // DEBUG

    /* Record the jump's IG and offset within it */
    id->idjIG   = emitCurIG;
    id->idjOffs = emitCurIGsize;

    /* Append this jump to this IG's jump list */
    id->idjNext      = emitCurIGjmpList;
    emitCurIGjmpList = id;

#if EMITTER_STATS
    emitTotalIGjmps++;
#endif

    id->idCodeSize(4);

    appendToCurIG(id);
}

void emitter::emitIns_J_cond_la(instruction ins, BasicBlock* dst, regNumber reg1, regNumber reg2)
{
    // TODO-RISCV64:
    //   Now the emitIns_J_cond_la() is only the short condition branch.
    //   There is no long condition branch for RISCV64 so far.
    //   For RISCV64 , the long condition branch is like this:
    //     --->  branch_condition  condition_target;     //here is the condition branch, short branch is enough.
    //     --->  jump jump_target; (this supporting the long jump.)
    //     condition_target:
    //     ...
    //     ...
    //     jump_target:
    //
    //
    // INS_OPTS_J_cond: placeholders.  1-ins.
    //   ins  reg1, reg2, dst

    assert(dst != nullptr);
    assert(dst->HasFlag(BBF_HAS_LABEL));

    instrDescJmp* id = emitNewInstrJmp();

    id->idIns(ins);
    id->idReg1(reg1);
    id->idReg2(reg2);
    id->idjShort = false;

    id->idInsOpt(INS_OPTS_J_cond);
    id->idAddr()->iiaBBlabel = dst;

    id->idjKeepLong = emitComp->fgInDifferentRegions(emitComp->compCurBB, dst);
#ifdef DEBUG
    if (emitComp->opts.compLongAddress) // Force long branches
        id->idjKeepLong = 1;
#endif // DEBUG

    /* Record the jump's IG and offset within it */
    id->idjIG   = emitCurIG;
    id->idjOffs = emitCurIGsize;

    /* Append this jump to this IG's jump list */
    id->idjNext      = emitCurIGjmpList;
    emitCurIGjmpList = id;

#if EMITTER_STATS
    emitTotalIGjmps++;
#endif

    id->idCodeSize(4);

    appendToCurIG(id);
}

static inline constexpr unsigned WordMask(uint8_t bits);

/*****************************************************************************
 *
 *  Emits load of 64-bit constant to register.
 *
 */
void emitter::emitLoadImmediate(emitAttr size, regNumber reg, ssize_t imm)
{
    assert(!EA_IS_RELOC(size));
    assert(isGeneralRegister(reg));

    if (isValidSimm12(imm))
    {
        emitIns_R_R_I(INS_addi, size, reg, REG_R0, imm & 0xFFF);
        return;
    }

    /* The following algorithm works based on the following equation:
     * `imm = high32 + offset1` OR `imm = high32 - offset2`
     *
     * high32 will be loaded with `lui + addiw`, while offset
     * will be loaded with `slli + addi` in 11-bits chunks
     *
     * First, determine at which position to partition imm into high32 and offset,
     * so that it yields the least instruction.
     * Where high32 = imm[y:x] and imm[63:y] are all zeroes or all ones.
     *
     * From the above equation, the value of offset1 & offset2 are:
     * -> offset1 = imm[x-1:0]
     * -> offset2 = ~(imm[x-1:0] - 1)
     * The smaller offset should yield the least instruction. (is this correct?) */

    // STEP 1: Determine x & y

    int x;
    int y;
    if (((uint64_t)imm >> 63) & 0b1)
    {
        // last one position from MSB
        y = 63 - BitOperations::LeadingZeroCount((uint64_t)~imm) + 1;
    }
    else
    {
        // last zero position from MSB
        y = 63 - BitOperations::LeadingZeroCount((uint64_t)imm) + 1;
    }
    if (imm & 0b1)
    {
        // first zero position from LSB
        x = BitOperations::TrailingZeroCount((uint64_t)~imm);
    }
    else
    {
        // first one position from LSB
        x = BitOperations::TrailingZeroCount((uint64_t)imm);
    }

    // STEP 2: Determine whether to utilize SRLI or not.

    /* SRLI can be utilized when the input has the following pattern:
     *
     * 0...01...10...x
     * <-n-><-m->
     *
     * It will emit instructions to load the left shifted immidiate then
     * followed by a single SRLI instruction.
     *
     * Since it adds 1 instruction, loading the new form should at least remove
     * two instruction. Two instructions can be removed IF:
     *  1. y - x > 31, AND
     *  2. (b - a) < 32, OR
     *  3. (b - a) - (y - x) >= 11
     *
     * Visualization aid:
     * - Original immidiate
     *   0...01...10...x
     *       y       <-x
     * - Left shifted immidiate
     *   1...10...x0...0
     *       b  <-a
     * */

    constexpr int absMaxInsCount  = instrDescLoadImm::absMaxInsCount;
    constexpr int prefMaxInsCount = 5;
    assert(prefMaxInsCount <= absMaxInsCount);

    // If we generate more instructions than the prefered maximum instruction count, we'll instead use emitDataConst +
    // emitIns_R_C combination.
    int insCountLimit = prefMaxInsCount;
    // If we are currently generating prolog / epilog, we are currently not inside a method block, therefore, we should
    // not use the emitDataConst + emitIns_R_C combination.
    if (emitComp->compGeneratingProlog || emitComp->compGeneratingEpilog)
    {
        insCountLimit = absMaxInsCount;
    }

    bool     utilizeSRLI     = false;
    int      srliShiftAmount = 0;
    uint64_t originalImm     = imm;
    bool     cond1           = (y - x) > 31;
    if ((((uint64_t)imm >> 63) & 0b1) == 0 && cond1)
    {
        srliShiftAmount  = BitOperations::LeadingZeroCount((uint64_t)imm);
        uint64_t tempImm = (uint64_t)imm << srliShiftAmount;
        int      m       = BitOperations::LeadingZeroCount(~tempImm);
        int      b       = 64 - m;
        int      a       = BitOperations::TrailingZeroCount(tempImm);
        bool     cond2   = (b - a) < 32;
        bool     cond3   = ((y - x) - (b - a)) >= 11;
        if (cond2 || cond3)
        {
            imm         = tempImm;
            y           = b;
            x           = a;
            utilizeSRLI = true;
            insCountLimit -= 1;
        }
    }

    assert(y >= x);
    assert((1 <= y) && (y <= 63));
    assert((1 <= x) && (x <= 63));

    if (y < 32)
    {
        y = 31;
        x = 0;
    }
    else if ((y - x) < 31)
    {
        y = x + 31;
    }
    else
    {
        x = y - 31;
    }

    uint32_t high32 = ((int64_t)imm >> x) & WordMask(32);

    // STEP 3: Determine whether to use high32 + offset1 or high32 - offset2

    /* TODO: Instead of using subtract / add mode, assume that we're always adding
     * 12-bit chunks. However, if we encounter such 12-bit chunk with MSB == 1,
     * add 1 to the previous chunk, and add the 12-bit chunk as is, which
     * essentially does a subtraction. It will generate the least instruction to
     * load offset.
     * See the following discussion:
     * https://github.com/dotnet/runtime/pull/113250#discussion_r1987576070 */

    uint32_t offset1        = imm & WordMask((uint8_t)x);
    uint32_t offset2        = (~(offset1 - 1)) & WordMask((uint8_t)x);
    uint32_t offset         = offset1;
    bool     isSubtractMode = false;

    if ((high32 == 0x7FFFFFFF) && (y != 63))
    {
        /* Handle corner case: we cannot do subtract mode if high32 == 0x7FFFFFFF
         * Since adding 1 to it will change the sign bit. Instead, shift x and y
         * to the left by one. */
        int      newX       = x + 1;
        uint32_t newOffset1 = imm & WordMask((uint8_t)newX);
        uint32_t newOffset2 = (~(newOffset1 - 1)) & WordMask((uint8_t)newX);
        if (newOffset2 < offset1)
        {
            x              = newX;
            high32         = ((int64_t)imm >> x) & WordMask(32);
            offset2        = newOffset2;
            isSubtractMode = true;
        }
    }
    else if (offset2 < offset1)
    {
        isSubtractMode = true;
    }

    if (isSubtractMode)
    {
        offset = offset2;
        high32 = (high32 + 1) & WordMask(32);
    }

    assert(absMaxInsCount >= 2);
    int         numberOfInstructions = 0;
    instruction ins[absMaxInsCount];
    int32_t     values[absMaxInsCount];

    // STEP 4: Generate instructions to load high32

    uint32_t upper    = (high32 >> 12) & WordMask(20);
    uint32_t lower    = high32 & WordMask(12);
    int      lowerMsb = (lower >> 11) & 0b1;
    if (lowerMsb == 1)
    {
        upper += 1;
        upper &= WordMask(20);
    }
    if (upper != 0)
    {
        ins[numberOfInstructions]    = INS_lui;
        values[numberOfInstructions] = ((upper >> 19) & 0b1) ? (upper + 0xFFF00000) : upper;
        numberOfInstructions += 1;
    }
    if (lower != 0)
    {
        ins[numberOfInstructions]    = INS_addiw;
        values[numberOfInstructions] = lower;
        numberOfInstructions += 1;
    }

    // STEP 5: Generate instructions to load offset in 11-bits chunks

    int chunkLsbPos = (x < 11) ? 0 : (x - 11);
    int shift       = (x < 11) ? x : 11;
    int chunkMask   = (x < 11) ? WordMask((uint8_t)x) : WordMask(11);
    while (true)
    {
        uint32_t chunk = (offset >> chunkLsbPos) & chunkMask;

        if (chunk != 0)
        {
            /* We could move our 11 bit chunk window to the right for as many as the
             * leading zeros.*/
            int leadingZerosOn11BitsChunk = 11 - (32 - BitOperations::LeadingZeroCount(chunk));
            if (leadingZerosOn11BitsChunk > 0)
            {
                int maxAdditionalShift =
                    (chunkLsbPos < leadingZerosOn11BitsChunk) ? chunkLsbPos : leadingZerosOn11BitsChunk;
                chunkLsbPos -= maxAdditionalShift;
                shift += maxAdditionalShift;
                chunk = (offset >> chunkLsbPos) & chunkMask;
            }

            numberOfInstructions += 2;
            if (numberOfInstructions > insCountLimit)
            {
                break;
            }
            ins[numberOfInstructions - 2]    = INS_slli;
            values[numberOfInstructions - 2] = shift;
            if (isSubtractMode)
            {
                ins[numberOfInstructions - 1]    = INS_addi;
                values[numberOfInstructions - 1] = -(int32_t)chunk;
            }
            else
            {
                ins[numberOfInstructions - 1]    = INS_addi;
                values[numberOfInstructions - 1] = chunk;
            }
            shift = 0;
        }
        if (chunkLsbPos == 0)
        {
            break;
        }
        shift += (chunkLsbPos < 11) ? chunkLsbPos : 11;
        chunkMask = (chunkLsbPos < 11) ? (chunkMask >> (11 - chunkLsbPos)) : WordMask(11);
        chunkLsbPos -= (chunkLsbPos < 11) ? chunkLsbPos : 11;
    }
    if (shift > 0)
    {
        numberOfInstructions += 1;
        if (numberOfInstructions <= insCountLimit)
        {
            ins[numberOfInstructions - 1]    = INS_slli;
            values[numberOfInstructions - 1] = shift;
        }
    }

    // STEP 6: Determine whether to use emitDataConst or emit generated instructions

    if (numberOfInstructions <= insCountLimit)
    {
        instrDescLoadImm* id = static_cast<instrDescLoadImm*>(emitNewInstrLoadImm(size, originalImm));
        id->idReg1(reg);
        memcpy(id->ins, ins, sizeof(instruction) * numberOfInstructions);
        memcpy(id->values, values, sizeof(int32_t) * numberOfInstructions);
        if (utilizeSRLI)
        {
            numberOfInstructions += 1;
            assert(numberOfInstructions < absMaxInsCount);
            id->ins[numberOfInstructions - 1]    = INS_srli;
            id->values[numberOfInstructions - 1] = srliShiftAmount;
        }
        id->idCodeSize(numberOfInstructions * 4);
        id->idIns(id->ins[numberOfInstructions - 1]);

        appendToCurIG(id);
    }
    else if (size == EA_PTRSIZE)
    {
        assert(!emitComp->compGeneratingProlog && !emitComp->compGeneratingEpilog);
        auto constAddr = emitDataConst(&originalImm, sizeof(long), sizeof(long), TYP_LONG);
        emitIns_R_C(INS_ld, EA_PTRSIZE, reg, REG_NA, emitComp->eeFindJitDataOffs(constAddr));
    }
    else
    {
        assert(false && "If number of instruction exceeds MAX_NUM_OF_LOAD_IMM_INS, imm must be 8 bytes");
    }
}

/*****************************************************************************
 *
 *  Add a call instruction (direct or indirect).
 *      argSize<0 means that the caller will pop the arguments
 *
 * The other arguments are interpreted depending on callType as shown:
 * Unless otherwise specified, ireg,xreg,xmul,disp should have default values.
 *
 * EC_FUNC_TOKEN       : addr is the method address
 *
 * If callType is one of these emitCallTypes, addr has to be NULL.
 * EC_INDIR_R          : "call ireg".
 *
 * noSafePoint - force not making this call a safe point in partially interruptible code
 *
 */

void emitter::emitIns_Call(const EmitCallParams& params)
{
    /* Sanity check the arguments depending on callType */

    assert(params.callType < EC_COUNT);
    assert((params.callType != EC_FUNC_TOKEN) ||
           (params.ireg == REG_NA && params.xreg == REG_NA && params.xmul == 0 && params.disp == 0));
    assert(params.callType < EC_INDIR_R || params.addr == nullptr || isValidSimm12((ssize_t)params.addr));
    assert(params.callType != EC_INDIR_R ||
           (params.ireg < REG_COUNT && params.xreg == REG_NA && params.xmul == 0 && params.disp == 0));

    // RISCV64 never uses these
    assert(params.xreg == REG_NA && params.xmul == 0 && params.disp == 0);

    // Our stack level should be always greater than the bytes of arguments we push. Just
    // a sanity test.
    assert((unsigned)std::abs(params.argSize) <= codeGen->genStackLevel);

    // Trim out any callee-trashed registers from the live set.
    regMaskTP savedSet  = emitGetGCRegsSavedOrModified(params.methHnd);
    regMaskTP gcrefRegs = params.gcrefRegs & savedSet;
    regMaskTP byrefRegs = params.byrefRegs & savedSet;

#ifdef DEBUG
    if (EMIT_GC_VERBOSE)
    {
        printf("Call: GCvars=%s ", VarSetOps::ToString(emitComp, params.ptrVars));
        dumpConvertedVarSet(emitComp, params.ptrVars);
        printf(", gcrefRegs=");
        printRegMaskInt(gcrefRegs);
        emitDispRegSet(gcrefRegs);
        printf(", byrefRegs=");
        printRegMaskInt(byrefRegs);
        emitDispRegSet(byrefRegs);
        printf("\n");
    }
#endif

    /* Managed RetVal: emit sequence point for the call */
    if (emitComp->opts.compDbgInfo && params.debugInfo.GetLocation().IsValid())
    {
        codeGen->genIPmappingAdd(IPmappingDscKind::Normal, params.debugInfo, false);
    }

    /*
        We need to allocate the appropriate instruction descriptor based
        on whether this is a direct/indirect call, and whether we need to
        record an updated set of live GC variables.
     */
    instrDesc* id;

    assert(params.argSize % REGSIZE_BYTES == 0);
    int argCnt = (int)(params.argSize / (int)REGSIZE_BYTES);

    if (params.callType >= EC_INDIR_R)
    {
        /* Indirect call, virtual calls */

        assert(params.callType == EC_INDIR_R);

        id = emitNewInstrCallInd(argCnt, params.disp, params.ptrVars, gcrefRegs, byrefRegs, params.retSize,
                                 params.secondRetSize, params.hasAsyncRet);
    }
    else
    {
        /* Helper/static/nonvirtual/function calls (direct or through handle),
           and calls to an absolute addr. */

        assert(params.callType == EC_FUNC_TOKEN);

        id = emitNewInstrCallDir(argCnt, params.ptrVars, gcrefRegs, byrefRegs, params.retSize, params.secondRetSize,
                                 params.hasAsyncRet);
    }

    /* Update the emitter's live GC ref sets */

    // If the method returns a GC ref, mark RBM_INTRET appropriately
    if (params.retSize == EA_GCREF)
    {
        gcrefRegs |= RBM_INTRET;
    }
    else if (params.retSize == EA_BYREF)
    {
        byrefRegs |= RBM_INTRET;
    }

    // If is a multi-register return method is called, mark RBM_INTRET_1 appropriately
    if (params.secondRetSize == EA_GCREF)
    {
        gcrefRegs |= RBM_INTRET_1;
    }
    else if (params.secondRetSize == EA_BYREF)
    {
        byrefRegs |= RBM_INTRET_1;
    }

    VarSetOps::Assign(emitComp, emitThisGCrefVars, params.ptrVars);
    emitThisGCrefRegs = gcrefRegs;
    emitThisByrefRegs = byrefRegs;

    // for the purpose of GC safepointing tail-calls are not real calls
    id->idSetIsNoGC(params.isJump || params.noSafePoint || emitNoGChelper(params.methHnd));

    /* Set the instruction - special case jumping a function */
    instruction ins;

    ins = INS_jalr; // jalr
    id->idIns(ins);

    id->idInsOpt(INS_OPTS_C);
    // TODO-RISCV64: maybe optimize.

    // INS_OPTS_C: placeholders.  1/2/4-ins:
    //   if (callType == EC_INDIR_R)
    //      jalr REG_R0/REG_RA, ireg, offset   <---- 1-ins
    //   else if (callType == EC_FUNC_TOKEN || callType == EC_FUNC_ADDR)
    //     if reloc:
    //             //pc + offset_38bits       # only when reloc.
    //      auipc t2, addr-hi20
    //      jalr r0/1, t2, addr-lo12
    //
    //     else:
    //      lui  t2, dst_offset_lo32-hi
    //      ori  t2, t2, dst_offset_lo32-lo
    //      lui  t2, dst_offset_hi32-lo
    //      jalr REG_R0/REG_RA, t2, 0

    /* Record the address: method, indirection, or funcptr */
    if (params.callType == EC_INDIR_R)
    {
        /* This is an indirect call (either a virtual call or func ptr call) */
        // assert(callType == EC_INDIR_R);

        id->idSetIsCallRegPtr();

        regNumber reg_jalr = params.isJump ? REG_R0 : REG_RA;
        id->idReg4(reg_jalr);
        id->idReg3(params.ireg); // NOTE: for EC_INDIR_R, using idReg3.
        id->idSmallCns(0);       // SmallCns will contain JALR's offset.
        if (params.addr != nullptr)
        {
            // If addr is not NULL, it must contain JALR's offset, which is set to the lower 12 bits of address.
            id->idSmallCns((size_t)params.addr);
        }
        assert(params.xreg == REG_NA);

        id->idCodeSize(4);
    }
    else
    {
        /* This is a simple direct call: "call helper/method/addr" */

        assert(params.callType == EC_FUNC_TOKEN);
        assert(params.addr != NULL);

        void* addr =
            (void*)(((size_t)params.addr) + (params.isJump ? 0 : 1)); // NOTE: low-bit0 is used for jalr ra/r0,rd,0
        id->idAddr()->iiaAddr = (BYTE*)addr;

        if (emitComp->opts.compReloc)
        {
            id->idSetIsDspReloc();
            id->idCodeSize(8);
        }
        else
        {
            id->idCodeSize(32);
        }
    }

#ifdef DEBUG
    if (EMIT_GC_VERBOSE)
    {
        if (id->idIsLargeCall())
        {
            printf("[%02u] Rec call GC vars = %s\n", id->idDebugOnlyInfo()->idNum,
                   VarSetOps::ToString(emitComp, ((instrDescCGCA*)id)->idcGCvars));
        }
    }
#endif // DEBUG

    if (m_debugInfoSize > 0)
    {
        INDEBUG(id->idDebugOnlyInfo()->idCallSig = params.sigInfo);
        id->idDebugOnlyInfo()->idMemCookie = reinterpret_cast<size_t>(params.methHnd); // method token
    }

#ifdef LATE_DISASM
    if (params.addr != nullptr)
    {
        codeGen->getDisAssembler().disSetMethod((size_t)params.addr, params.methHnd);
    }
#endif // LATE_DISASM

    appendToCurIG(id);
}

/*****************************************************************************
 *
 *  Output a call instruction.
 */

unsigned emitter::emitOutputCall(const insGroup* ig, BYTE* dst, instrDesc* id, code_t code)
{
    unsigned char callInstrSize = sizeof(code_t); // 4 bytes
    regMaskTP     gcrefRegs;
    regMaskTP     byrefRegs;

    VARSET_TP GCvars(VarSetOps::UninitVal());

    // Is this a "fat" call descriptor?
    if (id->idIsLargeCall())
    {
        instrDescCGCA* idCall = (instrDescCGCA*)id;
        gcrefRegs             = idCall->idcGcrefRegs;
        byrefRegs             = idCall->idcByrefRegs;
        VarSetOps::Assign(emitComp, GCvars, idCall->idcGCvars);
    }
    else
    {
        assert(!id->idIsLargeDsp());
        assert(!id->idIsLargeCns());

        gcrefRegs = emitDecodeCallGCregs(id);
        byrefRegs = 0;
        VarSetOps::AssignNoCopy(emitComp, GCvars, VarSetOps::MakeEmpty(emitComp));
    }

    /* We update the GC info before the call as the variables cannot be
        used by the call. Killing variables before the call helps with
        boundary conditions if the call is CORINFO_HELP_THROW - see bug 50029.
        If we ever track aliased variables (which could be used by the
        call), we would have to keep them alive past the call. */

    emitUpdateLiveGCvars(GCvars, dst);
#ifdef DEBUG
    // NOTEADD:
    // Output any delta in GC variable info, corresponding to the before-call GC var updates done above.
    if (EMIT_GC_VERBOSE || emitComp->opts.disasmWithGC)
    {
        emitDispGCVarDelta(); // define in emit.cpp
    }
#endif // DEBUG

    assert(id->idIns() == INS_jalr);
    if (id->idIsCallRegPtr())
    { // EC_INDIR_R
        ssize_t offset = id->idSmallCns();
        assert(isValidSimm12(offset));
        code = emitInsCode(id->idIns());
        code |= (code_t)id->idReg4() << 7;
        code |= (code_t)id->idReg3() << 15;
        code |= (code_t)offset << 20;
        emitOutput_Instr(dst, code);
    }
    else if (id->idIsReloc())
    {
        // pc + offset_32bits
        //
        //   auipc t2, addr-hi20
        //   jalr r0/1,t2,addr-lo12

        emitOutput_Instr(dst, 0x00000397);

        size_t addr = (size_t)(id->idAddr()->iiaAddr); // get addr.

        int reg2 = (int)(addr & 1);
        addr -= reg2;

        if (!emitComp->opts.compReloc)
        {
            assert(isValidSimm32(addr - (ssize_t)dst));
        }

        assert((addr & 1) == 0);

        dst += 4;
        emitGCregDeadUpd(REG_DEFAULT_HELPER_CALL_TARGET, dst);

#ifdef DEBUG
        code = emitInsCode(INS_auipc);
        assert((code | (REG_DEFAULT_HELPER_CALL_TARGET << 7)) == 0x00000397);
        assert((int)REG_DEFAULT_HELPER_CALL_TARGET == 7);
        code = emitInsCode(INS_jalr);
        assert(code == 0x00000067);
#endif
        emitOutput_Instr(dst, 0x00000067 | (REG_DEFAULT_HELPER_CALL_TARGET << 15) | reg2 << 7);

        emitRecordRelocation(dst - 4, (BYTE*)addr, IMAGE_REL_RISCV64_PC);
    }
    else
    {
        // lui  t2, dst_offset_hi32-hi
        // addi t2, t2, dst_offset_hi32-lo
        // slli t2, t2, 11
        // addi t2, t2, dst_offset_low32-hi
        // slli t2, t2, 11
        // addi t2, t2, dst_offset_low32-md
        // slli t2, t2, 10
        // jalr t2

        ssize_t imm = (ssize_t)(id->idAddr()->iiaAddr);
        assert((uint64_t)(imm >> 32) <= 0x7fff); // RISC-V Linux Kernel SV48

        int reg2 = (int)(imm & 1);
        imm -= reg2;

        UINT32 high = imm >> 32;
        code        = emitInsCode(INS_lui);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= ((code_t)((high + 0x800) >> 12) & 0xfffff) << 12;
        emitOutput_Instr(dst, code);
        dst += 4;

        emitGCregDeadUpd(REG_DEFAULT_HELPER_CALL_TARGET, dst);

        code = emitInsCode(INS_addi);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= (code_t)(high & 0xfff) << 20;
        emitOutput_Instr(dst, code);
        dst += 4;

        code = emitInsCode(INS_slli);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= (code_t)(11 << 20);
        emitOutput_Instr(dst, code);
        dst += 4;

        UINT32 low = imm & 0xffffffff;

        code = emitInsCode(INS_addi);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= ((low >> 21) & 0x7ff) << 20;
        emitOutput_Instr(dst, code);
        dst += 4;

        code = emitInsCode(INS_slli);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= (code_t)(11 << 20);
        emitOutput_Instr(dst, code);
        dst += 4;

        code = emitInsCode(INS_addi);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= ((low >> 10) & 0x7ff) << 20;
        emitOutput_Instr(dst, code);
        dst += 4;

        code = emitInsCode(INS_slli);
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= (code_t)(10 << 20);
        emitOutput_Instr(dst, code);
        dst += 4;

        code = emitInsCode(INS_jalr);
        code |= (code_t)reg2 << 7;
        code |= (code_t)REG_DEFAULT_HELPER_CALL_TARGET << 15;
        code |= (low & 0x3ff) << 20;
        // the offset default is 0;
        emitOutput_Instr(dst, code);
    }

    dst += 4;

    // If the method returns a GC ref, mark INTRET (A0) appropriately.
    if (id->idGCref() == GCT_GCREF)
    {
        gcrefRegs |= RBM_INTRET;
    }
    else if (id->idGCref() == GCT_BYREF)
    {
        byrefRegs |= RBM_INTRET;
    }

    // If is a multi-register return method is called, mark INTRET_1 (A1) appropriately
    if (id->idIsLargeCall())
    {
        instrDescCGCA* idCall = (instrDescCGCA*)id;
        if (idCall->idSecondGCref() == GCT_GCREF)
        {
            gcrefRegs |= RBM_INTRET_1;
        }
        else if (idCall->idSecondGCref() == GCT_BYREF)
        {
            byrefRegs |= RBM_INTRET_1;
        }
        if (idCall->hasAsyncContinuationRet())
        {
            gcrefRegs |= RBM_ASYNC_CONTINUATION_RET;
        }
    }

    // If the GC register set has changed, report the new set.
    if (gcrefRegs != emitThisGCrefRegs)
    {
        emitUpdateLiveGCregs(GCT_GCREF, gcrefRegs, dst);
    }
    // If the Byref register set has changed, report the new set.
    if (byrefRegs != emitThisByrefRegs)
    {
        emitUpdateLiveGCregs(GCT_BYREF, byrefRegs, dst);
    }

    // Some helper calls may be marked as not requiring GC info to be recorded.
    if (!id->idIsNoGC())
    {
        // On RISCV64, as on AMD64 and LOONGARCH64, we don't change the stack pointer to push/pop args.
        // So we're not really doing a "stack pop" here (note that "args" is 0), but we use this mechanism
        // to record the call for GC info purposes.  (It might be best to use an alternate call,
        // and protect "emitStackPop" under the EMIT_TRACK_STACK_DEPTH preprocessor variable.)
        emitStackPop(dst, /*isCall*/ true, callInstrSize, /*args*/ 0);

        // Do we need to record a call location for GC purposes?
        //
        if (!emitFullGCinfo)
        {
            emitRecordGCcall(dst, callInstrSize);
        }
    }
    if (id->idIsCallRegPtr())
    {
        callInstrSize = 1 << 2;
    }
    else
    {
        callInstrSize = id->idIsReloc() ? (2 << 2) : (8 << 2); // INS_OPTS_C: 2/9-ins.
    }

    return callInstrSize;
}

void emitter::emitJumpDistBind()
{
#ifdef DEBUG
    if (emitComp->verbose)
    {
        printf("*************** In emitJumpDistBind()\n");
    }
    if (EMIT_INSTLIST_VERBOSE)
    {
        printf("\nInstruction list before the jump distance binding:\n\n");
        emitDispIGlist(true);
    }
#endif

#if DEBUG_EMIT
    auto printJmpInfo = [this](const instrDescJmp* jmp, const insGroup* jmpIG, NATIVE_OFFSET extra,
                               UNATIVE_OFFSET srcInstrOffs, UNATIVE_OFFSET srcEncodingOffs, UNATIVE_OFFSET dstOffs,
                               NATIVE_OFFSET jmpDist, const char* direction) {
        assert(jmp->idDebugOnlyInfo() != nullptr);
        if (jmp->idDebugOnlyInfo()->idNum == (unsigned)INTERESTING_JUMP_NUM || INTERESTING_JUMP_NUM == 0)
        {
            const char* dirId = (strcmp(direction, "fwd") == 0) ? "[1]" : "[2]";
            if (INTERESTING_JUMP_NUM == 0)
            {
                printf("%s Jump %u:\n", dirId, jmp->idDebugOnlyInfo()->idNum);
            }
            printf("%s Jump  block is at %08X\n", dirId, jmpIG->igOffs);
            printf("%s Jump reloffset is %04X\n", dirId, jmp->idjOffs);
            printf("%s Jump source is at %08X\n", dirId, srcEncodingOffs);
            printf("%s Label block is at %08X\n", dirId, dstOffs);
            printf("%s Jump  dist. is    %04X\n", dirId, jmpDist);
            if (extra > 0)
            {
                printf("%s Dist excess [S] = %d  \n", dirId, extra);
            }
        }
        if (EMITVERBOSE)
        {
            printf("Estimate of %s jump [%08X/%03u]: %04X -> %04X = %04X\n", direction, dspPtr(jmp),
                   jmp->idDebugOnlyInfo()->idNum, srcInstrOffs, dstOffs, jmpDist);
        }
    };
#endif

    instrDescJmp* jmp;

    UNATIVE_OFFSET adjIG;
    UNATIVE_OFFSET adjSJ;
    insGroup*      lstIG;
#ifdef DEBUG
    insGroup* prologIG = emitPrologIG;
#endif // DEBUG

    // NOTE:
    //  bit0 of isLinkingEnd: indicating whether updating the instrDescJmp's size with the type INS_OPTS_J;
    //  bit1 of isLinkingEnd: indicating not needed updating the size while emitTotalCodeSize <= 0xfff or had
    //  updated;
    unsigned int isLinkingEnd = emitTotalCodeSize <= 0xfff ? 2 : 0;

    UNATIVE_OFFSET ssz = 0; // relative small jump's delay-slot.
    // small  jump max. neg distance
    NATIVE_OFFSET nsd = B_DIST_SMALL_MAX_NEG;
    // small  jump max. pos distance
    NATIVE_OFFSET maxPlaceholderSize =
        emitCounts_INS_OPTS_J * (6 << 2); // the max placeholder sizeof(INS_OPTS_JALR) - sizeof(INS_OPTS_J)
    NATIVE_OFFSET psd = B_DIST_SMALL_MAX_POS - maxPlaceholderSize;

    /*****************************************************************************/
    /* If the default small encoding is not enough, we start again here.     */
    /*****************************************************************************/

AGAIN:

#ifdef DEBUG
    emitCheckIGList();
#endif

#ifdef DEBUG
    insGroup*     lastIG = nullptr;
    instrDescJmp* lastSJ = nullptr;
#endif

    lstIG = nullptr;
    adjSJ = 0;
    adjIG = 0;

    for (jmp = emitJumpList; jmp; jmp = jmp->idjNext)
    {
        insGroup* jmpIG;
        insGroup* tgtIG;

        UNATIVE_OFFSET jsz; // size of the jump instruction in bytes

        NATIVE_OFFSET  extra;           // How far beyond the short jump range is this jump offset?
        UNATIVE_OFFSET srcInstrOffs;    // offset of the source instruction of the jump
        UNATIVE_OFFSET srcEncodingOffs; // offset of the source used by the instruction set to calculate the relative
                                        // offset of the jump
        UNATIVE_OFFSET dstOffs;
        NATIVE_OFFSET  jmpDist; // the relative jump distance, as it will be encoded

        /* Make sure the jumps are properly ordered */

#ifdef DEBUG
        assert(lastSJ == nullptr || lastIG != jmp->idjIG || lastSJ->idjOffs < (jmp->idjOffs + adjSJ));
        lastSJ = (lastIG == jmp->idjIG) ? jmp : nullptr;

        assert(lastIG == nullptr || lastIG->igNum <= jmp->idjIG->igNum || jmp->idjIG == prologIG ||
               emitNxtIGnum > unsigned(0xFFFF)); // igNum might overflow
        lastIG = jmp->idjIG;
#endif // DEBUG

        /* Get hold of the current jump size */

        jsz = jmp->idCodeSize();

        /* Get the group the jump is in */

        jmpIG = jmp->idjIG;

        /* Are we in a group different from the previous jump? */

        if (lstIG != jmpIG)
        {
            /* Were there any jumps before this one? */

            if (lstIG)
            {
                /* Adjust the offsets of the intervening blocks */

                do
                {
                    lstIG = lstIG->igNext;
                    assert(lstIG);
#ifdef DEBUG
                    if (EMITVERBOSE)
                    {
                        printf("Adjusted offset of " FMT_BB " from %04X to %04X\n", lstIG->igNum, lstIG->igOffs,
                               lstIG->igOffs + adjIG);
                    }
#endif // DEBUG
                    lstIG->igOffs += adjIG;
                    assert(IsCodeAligned(lstIG->igOffs));
                } while (lstIG != jmpIG);
            }

            /* We've got the first jump in a new group */
            adjSJ = 0;
            lstIG = jmpIG;
        }

        /* Apply any local size adjustment to the jump's relative offset */
        jmp->idjOffs += adjSJ;

        // If this is a jump via register, the instruction size does not change, so we are done.

        /* Have we bound this jump's target already? */

        if (jmp->idIsBound())
        {
            /* Does the jump already have the smallest size? */

            if (jmp->idjShort)
            {
                // We should not be jumping/branching across funclets/functions
                emitCheckFuncletBranch(jmp, jmpIG);

                continue;
            }

            tgtIG = jmp->idAddr()->iiaIGlabel;
        }
        else
        {
            /* First time we've seen this label, convert its target */

            tgtIG = (insGroup*)emitCodeGetCookie(jmp->idAddr()->iiaBBlabel);

#ifdef DEBUG
            if (EMITVERBOSE)
            {
                if (tgtIG)
                {
                    printf(" to %s\n", emitLabelString(tgtIG));
                }
                else
                {
                    printf("-- ERROR, no emitter cookie for " FMT_BB "; it is probably missing BBF_HAS_LABEL.\n",
                           jmp->idAddr()->iiaBBlabel->bbNum);
                }
            }
            assert(tgtIG);
#endif // DEBUG

            /* Record the bound target */

            jmp->idAddr()->iiaIGlabel = tgtIG;
            jmp->idSetIsBound();
        }

        // We should not be jumping/branching across funclets/functions
        emitCheckFuncletBranch(jmp, jmpIG);

        /*
            In the following distance calculations, if we're not actually
            scheduling the code (i.e. reordering instructions), we can
            use the actual offset of the jump (rather than the beg/end of
            the instruction group) since the jump will not be moved around
            and thus its offset is accurate.

            First we need to figure out whether this jump is a forward or
            backward one; to do this we simply look at the ordinals of the
            group that contains the jump and the target.
         */

        srcInstrOffs = jmpIG->igOffs + jmp->idjOffs;

        /* Note that the destination is always the beginning of an IG, so no need for an offset inside it */
        dstOffs = tgtIG->igOffs;

        srcEncodingOffs = srcInstrOffs + ssz; // Encoding offset of relative offset for small branch

        const char* direction = nullptr;
        if (jmpIG->igNum < tgtIG->igNum)
        {
            /* Forward jump */
            direction = "fwd";

            /* Adjust the target offset by the current delta. This is a worst-case estimate, as jumps between
               here and the target could be shortened, causing the actual distance to shrink.
             */
            dstOffs += adjIG;

            /* Compute the distance estimate */
            jmpDist = dstOffs - srcEncodingOffs;

            /* How much beyond the max. short distance does the jump go? */
            extra = jmpDist - psd;
        }
        else
        {
            /* Backward jump */
            direction = "bwd";

            /* Compute the distance estimate */
            jmpDist = srcEncodingOffs - dstOffs;

            /* How much beyond the max. short distance does the jump go? */
            extra = jmpDist + nsd;
        }

#if DEBUG_EMIT
        printJmpInfo(jmp, jmpIG, extra, srcInstrOffs, srcEncodingOffs, dstOffs, jmpDist, direction);
#endif // DEBUG_EMIT

        assert(jmpDist >= 0);
        assert(!(jmpDist & 0x3));

        if (!(isLinkingEnd & 0x2) && (extra > 0) &&
            (jmp->idInsOpt() == INS_OPTS_J || jmp->idInsOpt() == INS_OPTS_J_cond))
        {
            // transform INS_OPTS_J/INS_OPTS_J_cond jump when jmpDist exceed the maximum short distance
            instruction ins = jmp->idIns();
            assert((INS_jal <= ins) && (ins <= INS_bgeu));

            if (ins > INS_jalr || (ins < INS_jalr && ins > INS_j)) // jal < beqz < bnez < jalr <
                                                                   // beq/bne/blt/bltu/bge/bgeu
            {
                if (isValidSimm13(jmpDist + maxPlaceholderSize))
                {
                    continue;
                }
                // convert branch to opposite branch and jump
                int insCount = isValidSimm21(jmpDist + maxPlaceholderSize) ? 1 /*jal*/ : 2 /*auipc+jalr*/;
                extra        = insCount * sizeof(code_t);
            }
            else if (ins == INS_jal || ins == INS_j)
            {
                if (isValidSimm21(jmpDist + maxPlaceholderSize))
                {
                    continue;
                }
                // convert jal to auipc+jalr
                extra = sizeof(code_t);
            }
            else
            {
                unreached();
            }

            jmp->idInsOpt(INS_OPTS_JALR);
            jmp->idCodeSize(jmp->idCodeSize() + extra);
            jmpIG->igSize += (unsigned short)extra; // the placeholder sizeof(INS_OPTS_JALR) - sizeof(INS_OPTS_J).
            adjSJ += (UNATIVE_OFFSET)extra;
            adjIG += (UNATIVE_OFFSET)extra;
            emitTotalCodeSize += (UNATIVE_OFFSET)extra;
            jmpIG->igFlags |= IGF_UPD_ISZ;
            isLinkingEnd |= 0x1;
        }
    } // end for each jump

    if ((isLinkingEnd & 0x3) < 0x2)
    {
        // indicating the instrDescJmp's size of the type INS_OPTS_J had updated
        // after the first round and should iterate again to update.
        isLinkingEnd = 0x2;

        // Adjust offsets of any remaining blocks.
        for (; lstIG;)
        {
            lstIG = lstIG->igNext;
            if (!lstIG)
            {
                break;
            }
#ifdef DEBUG
            if (EMITVERBOSE)
            {
                printf("Adjusted offset of " FMT_BB " from %04X to %04X\n", lstIG->igNum, lstIG->igOffs,
                       lstIG->igOffs + adjIG);
            }
#endif // DEBUG

            lstIG->igOffs += adjIG;

            assert(IsCodeAligned(lstIG->igOffs));
        }
        goto AGAIN;
    }

#ifdef DEBUG
    if (EMIT_INSTLIST_VERBOSE)
    {
        printf("\nLabels list after the jump distance binding:\n\n");
        emitDispIGlist(false);
    }

    emitCheckIGList();
#endif // DEBUG
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 instruction
 */

unsigned emitter::emitOutput_Instr(BYTE* dst, code_t code) const
{
    assert(dst != nullptr);
    static_assert(sizeof(code_t) == 4, "code_t must be 4 bytes");
    memcpy(dst + writeableOffset, &code, sizeof(code));
    return sizeof(code_t);
}

static inline void assertCodeLength(size_t code, uint8_t size)
{
    assert((code >> size) == 0);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 R-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  R-Type layout:
 *  31-------25-24---20-19--15-14------12-11-----------7-6------------0
 *  | funct7   |  rs2  | rs1  |  funct3  |      rd      |   opcode    |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeRTypeInstr(
    unsigned opcode, unsigned rd, unsigned funct3, unsigned rs1, unsigned rs2, unsigned funct7)
{
    assertCodeLength(opcode, 7);
    assertCodeLength(rd, 5);
    assertCodeLength(funct3, 3);
    assertCodeLength(rs1, 5);
    assertCodeLength(rs2, 5);
    assertCodeLength(funct7, 7);

    return opcode | (rd << 7) | (funct3 << 12) | (rs1 << 15) | (rs2 << 20) | (funct7 << 25);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 I-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  I-Type layout:
 *  31------------20-19-----15-14------12-11-----------7-6------------0
 *  |   imm[11:0]   |   rs1   |  funct3  |      rd      |   opcode    |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeITypeInstr(
    unsigned opcode, unsigned rd, unsigned funct3, unsigned rs1, unsigned imm12)
{
    assertCodeLength(opcode, 7);
    assertCodeLength(rd, 5);
    assertCodeLength(funct3, 3);
    assertCodeLength(rs1, 5);
    // This assert may be triggered by the untrimmed signed integers. Please refer to the TrimSigned helpers
    assertCodeLength(imm12, 12);

    return opcode | (rd << 7) | (funct3 << 12) | (rs1 << 15) | (imm12 << 20);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 S-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  S-Type layout:
 *  31-------25-24---20-19--15-14------12-11-----------7-6------------0
 *  |imm[11:5] |  rs2  | rs1  |  funct3  |   imm[4:0]   |   opcode    |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeSTypeInstr(
    unsigned opcode, unsigned funct3, unsigned rs1, unsigned rs2, unsigned imm12)
{
    static constexpr unsigned kLoMask = 0x1f; // 0b00011111
    static constexpr unsigned kHiMask = 0x7f; // 0b01111111

    assertCodeLength(opcode, 7);
    assertCodeLength(funct3, 3);
    assertCodeLength(rs1, 5);
    assertCodeLength(rs2, 5);
    // This assert may be triggered by the untrimmed signed integers. Please refer to the TrimSigned helpers
    assertCodeLength(imm12, 12);

    unsigned imm12Lo = imm12 & kLoMask;
    unsigned imm12Hi = (imm12 >> 5) & kHiMask;

    return opcode | (imm12Lo << 7) | (funct3 << 12) | (rs1 << 15) | (rs2 << 20) | (imm12Hi << 25);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 U-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  U-Type layout:
 *  31---------------------------------12-11-----------7-6------------0
 *  |             imm[31:12]             |      rd      |   opcode    |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeUTypeInstr(unsigned opcode, unsigned rd, unsigned imm20)
{
    assertCodeLength(opcode, 7);
    assertCodeLength(rd, 5);
    // This assert may be triggered by the untrimmed signed integers. Please refer to the TrimSigned helpers
    assertCodeLength(imm20, 20);

    return opcode | (rd << 7) | (imm20 << 12);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 B-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  B-Type layout:
 *  31-------30-----25-24-20-19-15-14--12-11-------8----7----6--------0
 *  |imm[12]|imm[10:5]| rs2 | rs1 |funct3|  imm[4:1]|imm[11]| opcode  |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeBTypeInstr(
    unsigned opcode, unsigned funct3, unsigned rs1, unsigned rs2, unsigned imm13)
{
    static constexpr unsigned kLoSectionMask = 0x0f; // 0b00001111
    static constexpr unsigned kHiSectionMask = 0x3f; // 0b00111111
    static constexpr unsigned kBitMask       = 0x01;

    assertCodeLength(opcode, 7);
    assertCodeLength(funct3, 3);
    assertCodeLength(rs1, 5);
    assertCodeLength(rs2, 5);
    // This assert may be triggered by the untrimmed signed integers. Please refer to the TrimSigned helpers
    assertCodeLength(imm13, 13);
    assert((imm13 & 0x01) == 0);

    unsigned imm12          = imm13 >> 1;
    unsigned imm12LoSection = imm12 & kLoSectionMask;
    unsigned imm12LoBit     = (imm12 >> 10) & kBitMask;
    unsigned imm12HiSection = (imm12 >> 4) & kHiSectionMask;
    unsigned imm12HiBit     = (imm12 >> 11) & kBitMask;

    return opcode | (imm12LoBit << 7) | (imm12LoSection << 8) | (funct3 << 12) | (rs1 << 15) | (rs2 << 20) |
           (imm12HiSection << 25) | (imm12HiBit << 31);
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 J-Type instruction
 *
 *  Note: Instruction types as per RISC-V Spec, Chapter "RV32/64G Instruction Set Listings"
 *  J-Type layout:
 *  31-------30--------21----20---19----------12-11----7-6------------0
 *  |imm[20]| imm[10:1]  |imm[11]|  imm[19:12]  |  rd   |   opcode    |
 *  -------------------------------------------------------------------
 */

/*static*/ emitter::code_t emitter::insEncodeJTypeInstr(unsigned opcode, unsigned rd, unsigned imm21)
{
    static constexpr unsigned kHiSectionMask = 0x3ff; // 0b1111111111
    static constexpr unsigned kLoSectionMask = 0xff;  // 0b11111111
    static constexpr unsigned kBitMask       = 0x01;

    assertCodeLength(opcode, 7);
    assertCodeLength(rd, 5);
    // This assert may be triggered by the untrimmed signed integers. Please refer to the TrimSigned helpers
    assertCodeLength(imm21, 21);
    assert((imm21 & 0x01) == 0);

    unsigned imm20          = imm21 >> 1;
    unsigned imm20HiSection = imm20 & kHiSectionMask;
    unsigned imm20HiBit     = (imm20 >> 19) & kBitMask;
    unsigned imm20LoSection = (imm20 >> 11) & kLoSectionMask;
    unsigned imm20LoBit     = (imm20 >> 10) & kBitMask;

    return opcode | (rd << 7) | (imm20LoSection << 12) | (imm20LoBit << 20) | (imm20HiSection << 21) |
           (imm20HiBit << 31);
}

static constexpr unsigned kInstructionOpcodeMask = 0x7f;
static constexpr unsigned kInstructionFunct3Mask = 0x7000;
static constexpr unsigned kInstructionFunct5Mask = 0xf8000000;
static constexpr unsigned kInstructionFunct7Mask = 0xfe000000;
static constexpr unsigned kInstructionFunct2Mask = 0x06000000;

#ifdef DEBUG

/*static*/ void emitter::emitOutput_RTypeInstr_SanityCheck(instruction ins, regNumber rd, regNumber rs1, regNumber rs2)
{
    switch (ins)
    {
        case INS_add:
        case INS_sub:
        case INS_sll:
        case INS_slt:
        case INS_sltu:
        case INS_xor:
        case INS_srl:
        case INS_sra:
        case INS_or:
        case INS_and:
        case INS_addw:
        case INS_subw:
        case INS_sllw:
        case INS_srlw:
        case INS_sraw:
        case INS_mul:
        case INS_mulh:
        case INS_mulhsu:
        case INS_mulhu:
        case INS_div:
        case INS_divu:
        case INS_rem:
        case INS_remu:
        case INS_mulw:
        case INS_divw:
        case INS_divuw:
        case INS_remw:
        case INS_remuw:
            assert(isGeneralRegisterOrR0(rd));
            assert(isGeneralRegisterOrR0(rs1));
            assert(isGeneralRegisterOrR0(rs2));
            break;
        case INS_fsgnj_s:
        case INS_fsgnjn_s:
        case INS_fsgnjx_s:
        case INS_fmin_s:
        case INS_fmax_s:
        case INS_fsgnj_d:
        case INS_fsgnjn_d:
        case INS_fsgnjx_d:
        case INS_fmin_d:
        case INS_fmax_d:
            assert(isFloatReg(rd));
            assert(isFloatReg(rs1));
            assert(isFloatReg(rs2));
            break;
        case INS_feq_s:
        case INS_feq_d:
        case INS_flt_d:
        case INS_flt_s:
        case INS_fle_s:
        case INS_fle_d:
            assert(isGeneralRegisterOrR0(rd));
            assert(isFloatReg(rs1));
            assert(isFloatReg(rs2));
            break;
        case INS_fmv_w_x:
        case INS_fmv_d_x:
            assert(isFloatReg(rd));
            assert(isGeneralRegisterOrR0(rs1));
            assert(rs2 == 0);
            break;
        case INS_fmv_x_d:
        case INS_fmv_x_w:
        case INS_fclass_s:
        case INS_fclass_d:
            assert(isGeneralRegisterOrR0(rd));
            assert(isFloatReg(rs1));
            assert(rs2 == 0);
            break;
        default:
            NO_WAY("Illegal ins within emitOutput_RTypeInstr!");
            break;
    }
}

/*static*/ void emitter::emitOutput_ITypeInstr_SanityCheck(
    instruction ins, regNumber rd, regNumber rs1, unsigned immediate, unsigned opcode)
{
    switch (ins)
    {
        case INS_mov:
        case INS_jalr:
        case INS_lb:
        case INS_lh:
        case INS_lw:
        case INS_lbu:
        case INS_lhu:
        case INS_addi:
        case INS_slti:
        case INS_sltiu:
        case INS_xori:
        case INS_ori:
        case INS_andi:
        case INS_lwu:
        case INS_ld:
        case INS_addiw:
        case INS_csrrw:
        case INS_csrrs:
        case INS_csrrc:
            assert(isGeneralRegisterOrR0(rd));
            assert(isGeneralRegisterOrR0(rs1));
            assert((opcode & kInstructionFunct7Mask) == 0);
            break;
        case INS_flw:
        case INS_fld:
            assert(isFloatReg(rd));
            assert(isGeneralRegisterOrR0(rs1));
            assert((opcode & kInstructionFunct7Mask) == 0);
            break;
        case INS_slli:
        case INS_srli:
        case INS_srai:
            assert(immediate < 64);
            assert(isGeneralRegisterOrR0(rd));
            assert(isGeneralRegisterOrR0(rs1));
            break;
        case INS_slliw:
        case INS_srliw:
        case INS_sraiw:
            assert(immediate < 32);
            assert(isGeneralRegisterOrR0(rd));
            assert(isGeneralRegisterOrR0(rs1));
            break;
        case INS_csrrwi:
        case INS_csrrsi:
        case INS_csrrci:
            assert(isGeneralRegisterOrR0(rd));
            assert(rs1 < 32);
            assert((opcode & kInstructionFunct7Mask) == 0);
            break;
        case INS_fence:
        {
            assert(rd == REG_ZERO);
            assert(rs1 == REG_ZERO);
            ssize_t format = immediate >> 8;
            assert((format == 0) || (format == 0x8));
            assert((opcode & kInstructionFunct7Mask) == 0);
        }
        break;
        default:
            NO_WAY("Illegal ins within emitOutput_ITypeInstr!");
            break;
    }
}

/*static*/ void emitter::emitOutput_STypeInstr_SanityCheck(instruction ins, regNumber rs1, regNumber rs2)
{
    switch (ins)
    {
        case INS_sb:
        case INS_sh:
        case INS_sw:
        case INS_sd:
            assert(isGeneralRegister(rs1));
            assert(isGeneralRegisterOrR0(rs2));
            break;
        case INS_fsw:
        case INS_fsd:
            assert(isGeneralRegister(rs1));
            assert(isFloatReg(rs2));
            break;
        default:
            NO_WAY("Illegal ins within emitOutput_STypeInstr!");
            break;
    }
}

/*static*/ void emitter::emitOutput_UTypeInstr_SanityCheck(instruction ins, regNumber rd)
{
    switch (ins)
    {
        case INS_lui:
        case INS_auipc:
            assert(isGeneralRegisterOrR0(rd));
            break;
        default:
            NO_WAY("Illegal ins within emitOutput_UTypeInstr!");
            break;
    }
}

/*static*/ void emitter::emitOutput_BTypeInstr_SanityCheck(instruction ins, regNumber rs1, regNumber rs2)
{
    switch (ins)
    {
        case INS_beqz:
        case INS_bnez:
            assert((rs1 == REG_ZERO) || (rs2 == REG_ZERO));
            FALLTHROUGH;
        case INS_beq:
        case INS_bne:
        case INS_blt:
        case INS_bge:
        case INS_bltu:
        case INS_bgeu:
            assert(isGeneralRegisterOrR0(rs1));
            assert(isGeneralRegisterOrR0(rs2));
            break;
        default:
            NO_WAY("Illegal ins within emitOutput_BTypeInstr!");
            break;
    }
}

/*static*/ void emitter::emitOutput_JTypeInstr_SanityCheck(instruction ins, regNumber rd)
{
    switch (ins)
    {
        case INS_j:
            assert(rd == REG_ZERO);
            break;
        case INS_jal:
            assert(isGeneralRegisterOrR0(rd));
            break;
        default:
            NO_WAY("Illegal ins within emitOutput_JTypeInstr!");
            break;
    }
}

#endif // DEBUG

/*****************************************************************************
 *
 *  Casts an integral or float register from their identification number to
 *  theirs binary format. In case of the integral registers the encoded number
 *  is the register id. In case of the floating point registers the encoded
 *  number is shifted back by the floating point register base (32) (The
 *  instruction itself specifies whether the register contains floating
 *  point or integer, in their encoding they are indistinguishable)
 *
 */

/*static*/ unsigned emitter::castFloatOrIntegralReg(regNumber reg)
{
    static constexpr unsigned kRegisterMask = 0x1f;

    assert(isGeneralRegisterOrR0(reg) || isFloatReg(reg));

    return reg & kRegisterMask;
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 R-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_RTypeInstr(BYTE* dst, instruction ins, regNumber rd, regNumber rs1, regNumber rs2) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_RTypeInstr_SanityCheck(ins, rd, rs1, rs2);
#endif // DEBUG
    unsigned opcode = insCode & kInstructionOpcodeMask;
    unsigned funct3 = (insCode & kInstructionFunct3Mask) >> 12;
    unsigned funct7 = (insCode & kInstructionFunct7Mask) >> 25;
    return emitOutput_Instr(dst, insEncodeRTypeInstr(opcode, castFloatOrIntegralReg(rd), funct3,
                                                     castFloatOrIntegralReg(rs1), castFloatOrIntegralReg(rs2), funct7));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 I-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_ITypeInstr(BYTE* dst, instruction ins, regNumber rd, regNumber rs1, unsigned imm12) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_ITypeInstr_SanityCheck(ins, rd, rs1, imm12, insCode);
#endif // DEBUG
    unsigned opcode = insCode & kInstructionOpcodeMask;
    unsigned funct3 = (insCode & kInstructionFunct3Mask) >> 12;
    unsigned funct7 = (insCode & kInstructionFunct7Mask) >> 20; // only used by some of the immediate shifts
    return emitOutput_Instr(dst, insEncodeITypeInstr(opcode, castFloatOrIntegralReg(rd), funct3, rs1, imm12 | funct7));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 S-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_STypeInstr(BYTE* dst, instruction ins, regNumber rs1, regNumber rs2, unsigned imm12) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_STypeInstr_SanityCheck(ins, rs1, rs2);
#endif // DEBUG
    unsigned opcode = insCode & kInstructionOpcodeMask;
    unsigned funct3 = (insCode & kInstructionFunct3Mask) >> 12;
    return emitOutput_Instr(dst, insEncodeSTypeInstr(opcode, funct3, rs1, castFloatOrIntegralReg(rs2), imm12));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 U-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_UTypeInstr(BYTE* dst, instruction ins, regNumber rd, unsigned imm20) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_UTypeInstr_SanityCheck(ins, rd);
#endif // DEBUG
    return emitOutput_Instr(dst, insEncodeUTypeInstr(insCode, rd, imm20));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 B-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_BTypeInstr(BYTE* dst, instruction ins, regNumber rs1, regNumber rs2, unsigned imm13) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_BTypeInstr_SanityCheck(ins, rs1, rs2);
#endif // DEBUG
    unsigned opcode = insCode & kInstructionOpcodeMask;
    unsigned funct3 = (insCode & kInstructionFunct3Mask) >> 12;
    return emitOutput_Instr(dst, insEncodeBTypeInstr(opcode, funct3, rs1, rs2, imm13));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 B-Type instruction with inverted comparation to
 *  the given buffer. Returns a length of an encoded instruction opcode
 *
 *  Note: Replaces:
 *      - beqz with bnez and vice versa
 *      - beq with bne and vice versa
 *      - blt with bge and vice versa
 *      - bltu with bgeu and vice versa
 */

unsigned emitter::emitOutput_BTypeInstr_InvertComparation(
    BYTE* dst, instruction ins, regNumber rs1, regNumber rs2, unsigned imm13) const
{
    unsigned insCode = emitInsCode(ins) ^ 0x1000;
#ifdef DEBUG
    emitOutput_BTypeInstr_SanityCheck(ins, rs1, rs2);
#endif // DEBUG
    unsigned opcode = insCode & kInstructionOpcodeMask;
    unsigned funct3 = (insCode & kInstructionFunct3Mask) >> 12;
    return emitOutput_Instr(dst, insEncodeBTypeInstr(opcode, funct3, rs1, rs2, imm13));
}

/*****************************************************************************
 *
 *  Emit a 32-bit RISCV64 J-Type instruction to the given buffer. Returns a
 *  length of an encoded instruction opcode
 *
 */

unsigned emitter::emitOutput_JTypeInstr(BYTE* dst, instruction ins, regNumber rd, unsigned imm21) const
{
    unsigned insCode = emitInsCode(ins);
#ifdef DEBUG
    emitOutput_JTypeInstr_SanityCheck(ins, rd);
#endif // JTypeInstructionSanityCheck
    return emitOutput_Instr(dst, insEncodeJTypeInstr(insCode, rd, imm21));
}

void emitter::emitOutputInstrJumpDistanceHelper(const insGroup* ig,
                                                instrDescJmp*   jmp,
                                                UNATIVE_OFFSET& dstOffs,
                                                const BYTE*&    dstAddr) const
{
    if (jmp->idAddr()->iiaHasInstrCount())
    {
        assert(ig != nullptr);
        int      instrCount = jmp->idAddr()->iiaGetInstrCount();
        unsigned insNum     = emitFindInsNum(ig, jmp);
        if (instrCount < 0)
        {
            // Backward branches using instruction count must be within the same instruction group.
            assert(insNum + 1 >= static_cast<unsigned>(-instrCount));
        }
        dstOffs = ig->igOffs + emitFindOffset(ig, insNum + 1 + instrCount);
        dstAddr = emitOffsetToPtr(dstOffs);
        return;
    }
    dstOffs = jmp->idAddr()->iiaIGlabel->igOffs;
    dstAddr = emitOffsetToPtr(dstOffs);
}

/*****************************************************************************
 *
 *  Calculates a current jump instruction distance
 *
 */

ssize_t emitter::emitOutputInstrJumpDistance(const BYTE* src, const insGroup* ig, instrDescJmp* jmp)
{
    UNATIVE_OFFSET srcOffs = emitCurCodeOffs(src);
    const BYTE*    srcAddr = emitOffsetToPtr(srcOffs);

    assert(!jmp->idAddr()->iiaIsJitDataOffset()); // not used by riscv64 impl

    UNATIVE_OFFSET dstOffs{};
    const BYTE*    dstAddr = nullptr;
    emitOutputInstrJumpDistanceHelper(ig, jmp, dstOffs, dstAddr);

    ssize_t distVal = static_cast<ssize_t>(dstAddr - srcAddr);

    if (dstOffs > srcOffs)
    {
        // This is a forward jump

        emitFwdJumps = true;

        // The target offset will be closer by at least 'emitOffsAdj', but only if this
        // jump doesn't cross the hot-cold boundary.
        if (!emitJumpCrossHotColdBoundary(srcOffs, dstOffs))
        {
            distVal -= emitOffsAdj;
            dstOffs -= emitOffsAdj;
        }
        jmp->idjOffs = dstOffs;
        if (jmp->idjOffs != dstOffs)
        {
            IMPL_LIMITATION("Method is too large");
        }
    }
    return distVal;
}

static inline constexpr unsigned WordMask(uint8_t bits)
{
    return static_cast<unsigned>((1ull << bits) - 1);
}

template <uint8_t MaskSize>
static unsigned LowerNBitsOfWord(ssize_t word)
{
    static_assert(MaskSize < 32, "Given mask size is bigger than the word itself");
    static_assert(MaskSize > 0, "Given mask size cannot be zero");

    static constexpr unsigned kMask = WordMask(MaskSize);

    return static_cast<unsigned>(word & kMask);
}

template <uint8_t MaskSize>
static unsigned UpperNBitsOfWord(ssize_t word)
{
    static constexpr unsigned kShift = 32 - MaskSize;

    return LowerNBitsOfWord<MaskSize>(word >> kShift);
}

template <uint8_t MaskSize>
static unsigned UpperNBitsOfWordSignExtend(ssize_t word)
{
    static constexpr unsigned kSignExtend = 1 << (31 - MaskSize);

    return UpperNBitsOfWord<MaskSize>(word + kSignExtend);
}

static unsigned UpperWordOfDoubleWord(ssize_t immediate)
{
    return static_cast<unsigned>(immediate >> 32);
}

static unsigned LowerWordOfDoubleWord(ssize_t immediate)
{
    static constexpr size_t kWordMask = WordMask(32);

    return static_cast<unsigned>(immediate & kWordMask);
}

template <uint8_t UpperMaskSize, uint8_t LowerMaskSize>
static ssize_t DoubleWordSignExtend(ssize_t doubleWord)
{
    static constexpr size_t kLowerSignExtend = static_cast<size_t>(1) << (63 - LowerMaskSize);
    static constexpr size_t kUpperSignExtend = static_cast<size_t>(1) << (63 - UpperMaskSize);

    return doubleWord + (kLowerSignExtend | kUpperSignExtend);
}

template <uint8_t UpperMaskSize>
static ssize_t UpperWordOfDoubleWordSingleSignExtend(ssize_t doubleWord)
{
    static constexpr size_t kUpperSignExtend = static_cast<size_t>(1) << (31 - UpperMaskSize);

    return UpperWordOfDoubleWord(doubleWord + kUpperSignExtend);
}

template <uint8_t UpperMaskSize, uint8_t LowerMaskSize>
static ssize_t UpperWordOfDoubleWordDoubleSignExtend(ssize_t doubleWord)
{
    return UpperWordOfDoubleWord(DoubleWordSignExtend<UpperMaskSize, LowerMaskSize>(doubleWord));
}

/*static*/ unsigned emitter::TrimSignedToImm12(ssize_t imm12)
{
    assert(isValidSimm12(imm12));

    return static_cast<unsigned>(LowerNBitsOfWord<12>(imm12));
}

/*static*/ unsigned emitter::TrimSignedToImm13(ssize_t imm13)
{
    assert(isValidSimm13(imm13));

    return static_cast<unsigned>(LowerNBitsOfWord<13>(imm13));
}

/*static*/ unsigned emitter::TrimSignedToImm20(ssize_t imm20)
{
    assert(isValidSimm20(imm20));

    return static_cast<unsigned>(LowerNBitsOfWord<20>(imm20));
}

/*static*/ unsigned emitter::TrimSignedToImm21(ssize_t imm21)
{
    assert(isValidSimm21(imm21));

    return static_cast<unsigned>(LowerNBitsOfWord<21>(imm21));
}

BYTE* emitter::emitOutputInstr_OptsReloc(BYTE* dst, const instrDesc* id, instruction* ins)
{
    BYTE* const     dstBase = dst;
    const regNumber reg1    = id->idReg1();

    dst += emitOutput_UTypeInstr(dst, INS_auipc, reg1, 0);

    if (id->idIsCnsReloc())
    {
        *ins = INS_addi;
    }
    else
    {
        assert(id->idIsDspReloc());
        *ins = INS_ld;
    }

    dst += emitOutput_ITypeInstr(dst, *ins, reg1, reg1, 0);

    emitRecordRelocation(dstBase, id->idAddr()->iiaAddr, IMAGE_REL_RISCV64_PC);

    return dst;
}

BYTE* emitter::emitOutputInstr_OptsRc(BYTE* dst, const instrDesc* id, instruction* ins)
{
    assert(id->idAddr()->iiaIsJitDataOffset());
    assert(id->idGCref() == GCT_NONE);

    const int offset = id->idAddr()->iiaGetJitDataOffset();
    assert(offset >= 0);
    assert((UNATIVE_OFFSET)offset < emitDataSize());

    *ins                 = id->idIns();
    const regNumber reg1 = id->idReg1();
    assert(reg1 != REG_ZERO);
    assert(id->idCodeSize() == 2 * sizeof(code_t));
    const ssize_t immediate = (emitConsBlock - dst) + offset;
    assert((immediate > 0) && ((immediate & 0x03) == 0));
    assert(isValidSimm32(immediate));

    const regNumber tempReg = isFloatReg(reg1) ? codeGen->rsGetRsvdReg() : reg1;
    dst += emitOutput_UTypeInstr(dst, INS_auipc, tempReg, UpperNBitsOfWordSignExtend<20>(immediate));
    dst += emitOutput_ITypeInstr(dst, *ins, reg1, tempReg, LowerNBitsOfWord<12>(immediate));
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsRl(BYTE* dst, instrDesc* id, instruction* ins)
{
    insGroup* targetInsGroup = static_cast<insGroup*>(emitCodeGetCookie(id->idAddr()->iiaBBlabel));
    id->idAddr()->iiaIGlabel = targetInsGroup;

    const regNumber reg1   = id->idReg1();
    const ssize_t   igOffs = targetInsGroup->igOffs;

    *ins = INS_auipc;

    const ssize_t immediate = (emitCodeBlock - dst) + igOffs;
    assert((immediate & 0x03) == 0);
    assert(isValidSimm32(immediate));
    dst += emitOutput_UTypeInstr(dst, INS_auipc, reg1, UpperNBitsOfWordSignExtend<20>(immediate));
    dst += emitOutput_ITypeInstr(dst, INS_addi, reg1, reg1, LowerNBitsOfWord<12>(immediate));
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsJalr(BYTE* dst, instrDescJmp* jmp, const insGroup* ig, instruction* ins)
{
    const ssize_t immediate = emitOutputInstrJumpDistance(dst, ig, jmp) - 4;
    assert((immediate & 0x03) == 0);

    *ins = jmp->idIns();
    if (jmp->idInsIs(INS_jal, INS_j)) // far jump
    {
        assert(jmp->idCodeSize() == 2 * sizeof(code_t));
        assert(isValidSimm32(immediate));
        dst += emitOutput_UTypeInstr(dst, INS_auipc, REG_RA, UpperNBitsOfWordSignExtend<20>(immediate));
        dst += emitOutput_ITypeInstr(dst, INS_jalr, REG_RA, REG_RA, LowerNBitsOfWord<12>(immediate));
    }
    else // opposite branch + jump
    {
        assert(jmp->idInsIs(INS_beqz, INS_bnez, INS_beq, INS_bne, INS_blt, INS_bltu, INS_bge, INS_bgeu));
        regNumber reg2 = jmp->idInsIs(INS_beqz, INS_bnez) ? REG_R0 : jmp->idReg2();
        dst += emitOutput_BTypeInstr_InvertComparation(dst, jmp->idIns(), jmp->idReg1(), reg2, jmp->idCodeSize());
        if (jmp->idCodeSize() == 2 * sizeof(code_t))
        {
            dst += emitOutput_JTypeInstr(dst, INS_jal, REG_ZERO, TrimSignedToImm21(immediate));
        }
        else
        {
            assert(jmp->idCodeSize() == 3 * sizeof(code_t));
            assert(isValidSimm32(immediate));
            dst += emitOutput_UTypeInstr(dst, INS_auipc, REG_RA, UpperNBitsOfWordSignExtend<20>(immediate));
            dst += emitOutput_ITypeInstr(dst, INS_jalr, REG_ZERO, REG_RA, LowerNBitsOfWord<12>(immediate));
        }
    }
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsJCond(BYTE* dst, instrDesc* id, const insGroup* ig, instruction* ins)
{
    const ssize_t immediate = emitOutputInstrJumpDistance(dst, ig, static_cast<instrDescJmp*>(id));

    *ins = id->idIns();

    dst += emitOutput_BTypeInstr(dst, *ins, id->idReg1(), id->idReg2(), TrimSignedToImm13(immediate));
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsJ(BYTE* dst, instrDesc* id, const insGroup* ig, instruction* ins)
{
    const ssize_t immediate = emitOutputInstrJumpDistance(dst, ig, static_cast<instrDescJmp*>(id));
    assert((immediate & 0x03) == 0);

    *ins = id->idIns();

    switch (*ins)
    {
        case INS_jal:
            dst += emitOutput_JTypeInstr(dst, INS_jal, REG_RA, TrimSignedToImm21(immediate));
            break;
        case INS_j:
            dst += emitOutput_JTypeInstr(dst, INS_j, REG_ZERO, TrimSignedToImm21(immediate));
            break;
        case INS_jalr:
            dst += emitOutput_ITypeInstr(dst, INS_jalr, id->idReg1(), id->idReg2(), TrimSignedToImm12(immediate));
            break;
        case INS_bnez:
        case INS_beqz:
            dst += emitOutput_BTypeInstr(dst, *ins, id->idReg1(), REG_ZERO, TrimSignedToImm13(immediate));
            break;
        case INS_beq:
        case INS_bne:
        case INS_blt:
        case INS_bge:
        case INS_bltu:
        case INS_bgeu:
            dst += emitOutput_BTypeInstr(dst, *ins, id->idReg1(), id->idReg2(), TrimSignedToImm13(immediate));
            break;
        default:
            unreached();
            break;
    }
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsC(BYTE* dst, instrDesc* id, const insGroup* ig, size_t* size)
{
    if (id->idIsLargeCall())
    {
        *size = sizeof(instrDescCGCA);
    }
    else
    {
        assert(!id->idIsLargeDsp());
        assert(!id->idIsLargeCns());
        *size = sizeof(instrDesc);
    }
    dst += emitOutputCall(ig, dst, id, 0);
    return dst;
}

BYTE* emitter::emitOutputInstr_OptsI(BYTE* dst, instrDesc* id, instruction* lastIns)
{
    assert(id->idInsOpt() == INS_OPTS_I);

    instrDescLoadImm* idli   = static_cast<instrDescLoadImm*>(id);
    instruction*      ins    = idli->ins;
    int32_t*          values = idli->values;
    regNumber         reg    = idli->idReg1();

    assert((reg != REG_NA) && (reg != REG_R0));

    int numberOfInstructions = idli->idCodeSize() / sizeof(code_t);
    for (int i = 0; i < numberOfInstructions; i++)
    {
        if ((i == 0) && (ins[0] == INS_lui))
        {
            assert(isValidSimm20(values[i]));
            dst += emitOutput_UTypeInstr(dst, ins[i], reg, values[i] & 0xfffff);
        }
        else if ((i == 0) && ((ins[0] == INS_addiw) || (ins[0] == INS_addi)))
        {
            assert(isValidSimm12(values[i]) || ((ins[i] == INS_addiw) && isValidUimm12(values[i])));
            dst += emitOutput_ITypeInstr(dst, ins[i], reg, REG_R0, values[i] & 0xfff);
        }
        else if (i == 0)
        {
            assert(false && "First instruction must be lui / addiw / addi");
        }
        else if ((ins[i] == INS_addi) || (ins[i] == INS_addiw) || (ins[i] == INS_slli) || (ins[i] == INS_srli))
        {
            assert(isValidSimm12(values[i]) || ((ins[i] == INS_addiw) && isValidUimm12(values[i])));
            dst += emitOutput_ITypeInstr(dst, ins[i], reg, reg, values[i] & 0xfff);
        }
        else
        {
            assert(false && "Remaining instructions must be addi / addiw / slli / srli");
        }
    }

    *lastIns = ins[numberOfInstructions - 1];

    return dst;
}

/*****************************************************************************
 *
 *  Append the machine code corresponding to the given instruction descriptor
 *  to the code block at '*dp'; the base of the code block is 'bp', and 'ig'
 *  is the instruction group that contains the instruction. Updates '*dp' to
 *  point past the generated code, and returns the size of the instruction
 *  descriptor in bytes.
 */

size_t emitter::emitOutputInstr(insGroup* ig, instrDesc* id, BYTE** dp)
{
    BYTE*             dst  = *dp;
    BYTE*             dst2 = dst + 4;
    const BYTE* const odst = *dp;
    instruction       ins;
    size_t            sz = 0;

    static_assert(REG_NA == static_cast<int>(REG_NA), "REG_NA must fit in an int");

    insOpts insOp = id->idInsOpt();

    switch (insOp)
    {
        case INS_OPTS_RELOC:
            dst = emitOutputInstr_OptsReloc(dst, id, &ins);
            sz  = sizeof(instrDesc);
            break;
        case INS_OPTS_RC:
            dst = emitOutputInstr_OptsRc(dst, id, &ins);
            sz  = sizeof(instrDesc);
            break;
        case INS_OPTS_RL:
            dst = emitOutputInstr_OptsRl(dst, id, &ins);
            sz  = sizeof(instrDesc);
            break;
        case INS_OPTS_JALR:
            dst = emitOutputInstr_OptsJalr(dst, static_cast<instrDescJmp*>(id), ig, &ins);
            sz  = sizeof(instrDescJmp);
            break;
        case INS_OPTS_J_cond:
            dst = emitOutputInstr_OptsJCond(dst, id, ig, &ins);
            sz  = sizeof(instrDescJmp);
            break;
        case INS_OPTS_J:
            // jal/j/jalr/bnez/beqz/beq/bne/blt/bge/bltu/bgeu dstRW-relative.
            dst = emitOutputInstr_OptsJ(dst, id, ig, &ins);
            sz  = sizeof(instrDescJmp);
            break;
        case INS_OPTS_C:
            dst  = emitOutputInstr_OptsC(dst, id, ig, &sz);
            dst2 = dst;
            ins  = INS_nop;
            break;
        case INS_OPTS_I:
            dst = emitOutputInstr_OptsI(dst, id, &ins);
            sz  = sizeof(instrDescLoadImm);
            break;
        default: // case INS_OPTS_NONE:
            dst += emitOutput_Instr(dst, id->idAddr()->iiaGetInstrEncode());
            ins = id->idIns();
            sz  = sizeof(instrDesc);
            break;
    }

    // Determine if any registers now hold GC refs, or whether a register that was overwritten held a GC ref.
    // We assume here that "id->idGCref()" is not GC_NONE only if the instruction described by "id" writes a
    // GC ref to register "id->idReg1()".  (It may, apparently, also not be GC_NONE in other cases, such as
    // for stores, but we ignore those cases here.)
    if (emitInsMayWriteToGCReg(ins)) // True if "id->idIns()" writes to a register than can hold GC ref.
    {
        // We assume that "idReg1" is the primary destination register for all instructions
        if (id->idGCref() != GCT_NONE)
        {
            emitGCregLiveUpd(id->idGCref(), id->idReg1(), dst2);
        }
        else
        {
            emitGCregDeadUpd(id->idReg1(), dst2);
        }
    }

    // Now we determine if the instruction has written to a (local variable) stack location, and either written a GC
    // ref or overwritten one.
    if (emitInsWritesToLclVarStackLoc(id) /*|| emitInsWritesToLclVarStackLocPair(id)*/)
    {
        int      varNum = id->idAddr()->iiaLclVar.lvaVarNum();
        unsigned ofs    = AlignDown(id->idAddr()->iiaLclVar.lvaOffset(), TARGET_POINTER_SIZE);
        bool     FPbased;
        int      adr = emitComp->lvaFrameAddress(varNum, &FPbased);
        if (id->idGCref() != GCT_NONE)
        {
            emitGCvarLiveUpd(adr + ofs, varNum, id->idGCref(), dst2 DEBUG_ARG(varNum));
        }
        else
        {
            // If the type of the local is a gc ref type, update the liveness.
            var_types vt;
            if (varNum >= 0)
            {
                // "Regular" (non-spill-temp) local.
                vt = var_types(emitComp->lvaTable[varNum].lvType);
            }
            else
            {
                TempDsc* tmpDsc = codeGen->regSet.tmpFindNum(varNum);
                vt              = tmpDsc->tdTempType();
            }
            if (vt == TYP_REF || vt == TYP_BYREF)
                emitGCvarDeadUpd(adr + ofs, dst2 DEBUG_ARG(varNum));
        }
    }

#ifdef DEBUG
    /* Make sure we set the instruction descriptor size correctly */

    if (emitComp->opts.disAsm || emitComp->verbose)
    {
#if DUMP_GC_TABLES
        bool dspOffs = emitComp->opts.dspGCtbls;
#else  // !DUMP_GC_TABLES
        bool dspOffs = !emitComp->opts.disDiffable;
#endif // !DUMP_GC_TABLES
        emitDispIns(id, false, dspOffs, true, emitCurCodeOffs(odst), *dp, (dst - odst), ig);
    }

    if (emitComp->compDebugBreak)
    {
        // For example, set JitBreakEmitOutputInstr=a6 will break when this method is called for
        // emitting instruction a6, (i.e. IN00a6 in jitdump).
        if ((unsigned)JitConfig.JitBreakEmitOutputInstr() == id->idDebugOnlyInfo()->idNum)
        {
            assert(!"JitBreakEmitOutputInstr reached");
        }
    }

    // Output any delta in GC info.
    if (EMIT_GC_VERBOSE || emitComp->opts.disasmWithGC)
    {
        emitDispGCInfoDelta();
    }

#else  // !DEBUG
    if (emitComp->opts.disAsm)
    {
        emitDispIns(id, false, false, true, emitCurCodeOffs(odst), *dp, (dst - odst), ig);
    }
#endif // !DEBUG

    /* All instructions are expected to generate code */

    assert(*dp != dst);

    *dp = dst;

    return sz;
}

/*****************************************************************************/
/*****************************************************************************/

// clang-format off
static const char* const RegNames[] =
{
    #define REGDEF(name, rnum, mask, sname) sname,
    #include "register.h"
};
// clang-format on

bool emitter::emitDispBranchInstrType(unsigned opcode2, bool is_zero_reg, bool& print_second_reg) const
{
    print_second_reg = true;
    switch (opcode2)
    {
        case 0:
            printf(is_zero_reg ? "beqz" : "beq ");
            print_second_reg = !is_zero_reg;
            break;
        case 1:
            printf(is_zero_reg ? "bnez" : "bne ");
            print_second_reg = !is_zero_reg;
            break;
        case 4:
            printf("blt ");
            break;
        case 5:
            printf("bge ");
            break;
        case 6:
            printf("bltu");
            break;
        case 7:
            printf("bgeu");
            break;
        default:
            return false;
    }
    return true;
}

void emitter::emitDispBranchOffset(const instrDesc* id, const insGroup* ig) const
{
    int instrCount = id->idAddr()->iiaGetInstrCount();
    if (ig == nullptr)
    {
        printf("pc%+d instructions", instrCount);
        return;
    }
    unsigned insNum = emitFindInsNum(ig, id);

    if (ig->igInsCnt < insNum + 1 + instrCount)
    {
        // TODO-RISCV64-BUG: This should be a labeled offset but does not contain an iiaIGlabel
        printf("pc%+d instructions", instrCount);
        return;
    }

    UNATIVE_OFFSET srcOffs = ig->igOffs + emitFindOffset(ig, insNum + 1);
    UNATIVE_OFFSET dstOffs = ig->igOffs + emitFindOffset(ig, insNum + 1 + instrCount);
    ssize_t        relOffs = static_cast<ssize_t>(emitOffsetToPtr(dstOffs) - emitOffsetToPtr(srcOffs));
    printf("pc%+d (%d instructions)", static_cast<int>(relOffs), instrCount);
}

void emitter::emitDispBranchLabel(const instrDesc* id) const
{
    if (id->idIsBound())
    {
        return emitPrintLabel(id->idAddr()->iiaIGlabel);
    }
    printf("L_M%03u_", FMT_BB, emitComp->compMethodID, id->idAddr()->iiaBBlabel->bbNum);
}

bool emitter::emitDispBranch(
    unsigned opcode2, unsigned rs1, unsigned rs2, const instrDesc* id, const insGroup* ig) const
{
    bool print_second_reg = true;
    if (!emitDispBranchInstrType(opcode2, rs2 == REG_ZERO, print_second_reg))
    {
        return false;
    }
    printf("           %s, ", RegNames[rs1]);
    if (print_second_reg)
    {
        printf("%s, ", RegNames[rs2]);
    }
    assert(id != nullptr);
    if (id->idAddr()->iiaHasInstrCount())
    {
        // Branch is jumping to some non-labeled offset
        emitDispBranchOffset(id, ig);
    }
    else
    {
        // Branch is jumping to the labeled offset
        emitDispBranchLabel(id);
    }
    printf("\n");
    return true;
}

void emitter::emitDispIllegalInstruction(code_t instructionCode)
{
    printf("RISCV64 illegal instruction: 0x%08X\n", instructionCode);
    assert(!"RISCV64 illegal instruction");
}

void emitter::emitDispImmediate(ssize_t imm, bool newLine /*= true*/, unsigned regBase /*= REG_ZERO*/)
{
    if (emitComp->opts.disDiffable && (regBase != REG_FP) && (regBase != REG_SP))
    {
        printf("0xD1FFAB1E");
    }
    else
    {
        printf("%li", imm);
    }

    if (newLine)
        printf("\n");
}

//----------------------------------------------------------------------------------------
// Disassemble the given instruction.
// The `emitter::emitDispInsName` is focused on the most important for debugging.
// So it implemented as far as simply and independently which is very useful for
// porting easily to the release mode.
//
// Arguments:
//    code - The instruction's encoding.
//    addr - The address of the code.
//    doffs - Flag informing whether the instruction's offset should be displayed.
//    insOffset - The instruction's offset.
//    id   - The instrDesc of the code if needed.
//    ig   - The insGroup of the code if needed
//
// Note:
//    The length of the instruction's name include aligned space is 15.
//

void emitter::emitDispInsName(
    code_t code, const BYTE* addr, bool doffs, unsigned insOffset, const instrDesc* id, const insGroup* ig)
{
    static constexpr int kMaxInstructionLength = 14;

    const BYTE* insAdr = addr - writeableOffset;
    emitDispInsAddr(insAdr);
    emitDispInsOffs(insOffset, doffs);

    if (emitComp->opts.disCodeBytes && !emitComp->opts.disDiffable)
        printf("  %08X    ", code);

    printf("      ");

    bool willPrintLoadImmValue = (id->idInsOpt() == INS_OPTS_I) && !emitComp->opts.disDiffable;

    switch (GetMajorOpcode(code))
    {
        case MajorOpcode::Lui:
        {
            const char* rd    = RegNames[(code >> 7) & 0x1f];
            int         imm20 = (code >> 12) & 0xfffff;
            if (imm20 & 0x80000)
            {
                imm20 |= 0xfff00000;
            }
            printf("lui            %s, ", rd);
            emitDispImmediate(imm20, !willPrintLoadImmValue);
            return;
        }
        case MajorOpcode::Auipc:
        {
            const char* rd    = RegNames[(code >> 7) & 0x1f];
            int         imm20 = (code >> 12) & 0xfffff;
            if (imm20 & 0x80000)
            {
                imm20 |= 0xfff00000;
            }
            printf("auipc          %s, ", rd);
            emitDispImmediate(imm20);
            return;
        }
        case MajorOpcode::OpImm:
        {
            unsigned opcode2      = (code >> 12) & 0x7;
            unsigned rd           = (code >> 7) & 0x1f;
            unsigned rs1          = (code >> 15) & 0x1f;
            int      imm12        = static_cast<int>(code) >> 20;
            bool     hasImmediate = true;
            int      printLength  = 0;

            switch (opcode2)
            {
                case 0x0: // ADDI & MV & NOP
                    if (code == emitInsCode(INS_nop))
                    {
                        printf("nop\n");
                        return;
                    }
                    else if (imm12 != 0)
                    {
                        printLength = printf("addi");
                    }
                    else
                    {
                        printLength  = printf("mv");
                        hasImmediate = false;
                    }
                    break;
                case 0x1:
                {
                    unsigned funct6 = (imm12 >> 6) & 0x3f;
                    unsigned shamt  = imm12 & 0x3f; // 6 BITS for SHAMT in RISCV6
                    switch (funct6)
                    {
                        case 0b011000:
                        {
                            static const char* names[] = {"clz", "ctz", "cpop", nullptr, "sext.b", "sext.h"};
                            // shift amount is treated as additional funct opcode
                            if (shamt >= ARRAY_SIZE(names) || shamt == 3)
                                return emitDispIllegalInstruction(code);

                            assert(names[shamt] != nullptr);
                            printLength  = printf("%s", names[shamt]);
                            hasImmediate = false;
                            break;
                        }
                        case 0b000000:
                            printLength = printf("slli");
                            imm12       = shamt;
                            break;

                        default:
                            return emitDispIllegalInstruction(code);
                    }
                }
                break;
                case 0x2: // SLTI
                    printLength = printf("slti");
                    break;
                case 0x3: // SLTIU
                    printLength = printf("sltiu");
                    break;
                case 0x4: // XORI
                    if (imm12 == -1)
                    {
                        printLength  = printf("not");
                        hasImmediate = false;
                    }
                    else
                    {
                        printLength = printf("xori");
                    }
                    break;
                case 0x5: // SRLI & SRAI
                {
                    unsigned funct6 = (imm12 >> 6) & 0x3f;
                    imm12 &= 0x3f; // 6BITS for SHAMT in RISCV64
                    switch (funct6)
                    {
                        case 0b000000:
                            printLength = printf("srli");
                            break;
                        case 0b010000:
                            printLength = printf("srai");
                            break;
                        case 0b011000:
                            printLength = printf("rori");
                            break;
                        case 0b011010:
                            if (imm12 != 0b111000) // shift amount is treated as additional funct opcode
                                return emitDispIllegalInstruction(code);

                            printLength  = printf("rev8");
                            hasImmediate = false;
                            break;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                }
                break;
                case 0x6: // ORI
                    printLength = printf("ori");
                    break;
                case 0x7: // ANDI
                    printLength = printf("andi");
                    break;
                default:
                    return emitDispIllegalInstruction(code);
            }
            assert(printLength > 0);
            int paddingLength = kMaxInstructionLength - printLength;

            printf("%*s %s, %s", paddingLength, "", RegNames[rd], RegNames[rs1]);
            if (hasImmediate)
            {
                printf(", ");
                if (opcode2 == 0x0) // ADDI
                {
                    emitDispImmediate(imm12, false, rs1);
                }
                else
                {
                    printf("%d", imm12);
                }
            }
            if (!willPrintLoadImmValue)
            {
                printf("\n");
            }

            return;
        }
        case MajorOpcode::OpImm32:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            const char*  rd      = RegNames[(code >> 7) & 0x1f];
            const char*  rs1     = RegNames[(code >> 15) & 0x1f];
            int          imm12   = (((int)code) >> 20);
            switch (opcode2)
            {
                case 0x0: // ADDIW & SEXT.W
                    if (imm12 == 0)
                    {
                        printf("sext.w         %s, %s\n", rd, rs1);
                    }
                    else
                    {
                        printf("addiw          %s, %s, ", rd, rs1);
                        emitDispImmediate(imm12, !willPrintLoadImmValue);
                    }
                    return;
                case 0x1: // SLLIW, SLLI.UW, CLZW, CTZW, & CPOPW
                {
                    static constexpr unsigned kSlliwFunct7  = 0b0000000;
                    static constexpr unsigned kSlliUwFunct6 = 0b000010;

                    unsigned funct7 = (imm12 >> 5) & 0x7f;
                    unsigned funct6 = (imm12 >> 6) & 0x3f;
                    // SLLIW's instruction code's upper 7 bits have to be equal to zero
                    if (funct7 == kSlliwFunct7)
                    {
                        printf("slliw          %s, %s, %d\n", rd, rs1, imm12 & 0x1f); // 5 BITS for SHAMT in RISCV64
                    }
                    // SLLI.UW's instruction code's upper 6 bits have to be equal to 0b000010
                    else if (funct6 == kSlliUwFunct6)
                    {
                        printf("slli.uw        %s, %s, %d\n", rd, rs1, imm12 & 0x3f); // 6 BITS for SHAMT in RISCV64
                    }
                    else if (funct7 == 0b0110000)
                    {
                        static const char* names[] = {"clzw ", "ctzw ", "cpopw"};
                        // shift amount is treated as funct additional opcode bits
                        unsigned shamt = imm12 & 0x1f; // 5 BITS for SHAMT in RISCV64
                        if (shamt >= ARRAY_SIZE(names))
                            return emitDispIllegalInstruction(code);

                        printf("%s          %s, %s\n", names[shamt], rd, rs1);
                    }
                    else
                    {
                        emitDispIllegalInstruction(code);
                    }
                }
                    return;
                case 0x5: // SRLIW & SRAIW
                {
                    unsigned funct7 = (imm12 >> 5) & 0x7f;
                    imm12 &= 0x1f; // 5BITS for SHAMT in RISCV64
                    switch (funct7)
                    {
                        case 0b0000000:
                            printf("srliw          %s, %s, %d\n", rd, rs1, imm12);
                            return;
                        case 0b0100000:
                            printf("sraiw          %s, %s, %d\n", rd, rs1, imm12);
                            return;
                        case 0b0110000:
                            printf("roriw          %s, %s, %d\n", rd, rs1, imm12);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                }
                    return;
                default:
                    return emitDispIllegalInstruction(code);
            }
        }
        case MajorOpcode::Op:
        {
            unsigned int opcode2 = (code >> 25) & 0x7f;
            unsigned int opcode3 = (code >> 12) & 0x7;
            const char*  rd      = RegNames[(code >> 7) & 0x1f];
            const char*  rs1     = RegNames[(code >> 15) & 0x1f];
            const char*  rs2     = RegNames[(code >> 20) & 0x1f];

            switch (opcode2)
            {
                case 0b0000000:
                    switch (opcode3)
                    {
                        case 0x0: // ADD
                            printf("add            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x1: // SLL
                            printf("sll            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x2: // SLT
                            printf("slt            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x3: // SLTU
                            printf("sltu           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // XOR
                            printf("xor            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // SRL
                            printf("srl            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // OR
                            printf("or             %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x7: // AND
                            printf("and            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0100000:
                    switch (opcode3)
                    {
                        case 0x0: // SUB
                            printf("sub            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // XNOR
                            printf("xnor           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // SRA
                            printf("sra            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // ORN
                            printf("orn            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x7: // ANDN
                            printf("andn           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0000001:
                    switch (opcode3)
                    {
                        case 0x0: // MUL
                            printf("mul            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x1: // MULH
                            printf("mulh           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x2: // MULHSU
                            printf("mulhsu         %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x3: // MULHU
                            printf("mulhu          %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // DIV
                            printf("div            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // DIVU
                            printf("divu           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // REM
                            printf("rem            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x7: // REMU
                            printf("remu           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0010000:
                    switch (opcode3)
                    {
                        case 0x2: // SH1ADD
                            printf("sh1add         %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // SH2ADD
                            printf("sh2add         %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // SH3ADD
                            printf("sh3add         %s, %s, %s\n", rd, rs1, rs2);
                            return;
                    }
                    return;
                case 0b0110000:
                    switch (opcode3)
                    {
                        case 0b001:
                            printf("rol            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0b101:
                            printf("ror            %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0000101:
                {
                    if ((opcode3 >> 2) != 1) // clmul[h] unsupported
                        return emitDispIllegalInstruction(code);

                    static const char names[][5] = {"min ", "minu", "max ", "maxu"};
                    printf("%s           %s, %s, %s\n", names[opcode3 & 0b11], rd, rs1, rs2);
                    return;
                }
                default:
                    return emitDispIllegalInstruction(code);
            }
        }
        case MajorOpcode::Op32:
        {
            unsigned int opcode2 = (code >> 25) & 0x7f;
            unsigned int opcode3 = (code >> 12) & 0x7;
            unsigned int rs2Num  = (code >> 20) & 0x1f;
            const char*  rd      = RegNames[(code >> 7) & 0x1f];
            const char*  rs1     = RegNames[(code >> 15) & 0x1f];
            const char*  rs2     = RegNames[rs2Num];

            switch (opcode2)
            {
                case 0b0000000:
                    switch (opcode3)
                    {
                        case 0x0: // ADDW
                            printf("addw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x1: // SLLW
                            printf("sllw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // SRLW
                            printf("srlw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0100000:
                    switch (opcode3)
                    {
                        case 0x0: // SUBW
                            printf("subw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // SRAW
                            printf("sraw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0000001:
                    switch (opcode3)
                    {
                        case 0x0: // MULW
                            printf("mulw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // DIVW
                            printf("divw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x5: // DIVUW
                            printf("divuw          %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // REMW
                            printf("remw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x7: // REMUW
                            printf("remuw          %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0010000:
                    switch (opcode3)
                    {
                        case 0x2: // SH1ADD.UW
                            printf("sh1add.uw      %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x4: // SH2ADD.UW
                            printf("sh2add.uw      %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0x6: // SH3ADD.UW
                            printf("sh3add.uw      %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0110000:
                    switch (opcode3)
                    {
                        case 0b001:
                            printf("rolw           %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        case 0b101:
                            printf("rorw          %s, %s, %s\n", rd, rs1, rs2);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;
                case 0b0000100:
                    switch (opcode3)
                    {
                        case 0b000: // ZEXT.W & ADD.UW
                            if (rs2Num == REG_ZERO)
                            {
                                printf("zext.w         %s, %s\n", rd, rs1);
                            }
                            else
                            {
                                printf("add.uw         %s, %s, %s\n", rd, rs1, rs2);
                            }
                            return;
                        case 0b100: // ZEXT.H
                            // Note: zext.h is encoded as a pseudo for 'packw rd, rs1, zero' which is not in Zbb.
                            if (rs2Num != REG_ZERO)
                                return emitDispIllegalInstruction(code);

                            printf("zext.h         %s, %s\n", rd, rs1);
                            return;
                        default:
                            return emitDispIllegalInstruction(code);
                    }
                    return;

                default:
                    return emitDispIllegalInstruction(code);
            }
        }
        case MajorOpcode::Store:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            if (opcode2 >= 4)
                return emitDispIllegalInstruction(code);

            unsigned    rs1Num = (code >> 15) & 0x1f;
            const char* rs1    = RegNames[rs1Num];
            const char* rs2    = RegNames[(code >> 20) & 0x1f];
            int         offset = (((code >> 25) & 0x7f) << 5) | ((code >> 7) & 0x1f);
            if (offset & 0x800)
            {
                offset |= 0xfffff000;
            }

            char width = "bhwd"[opcode2];
            printf("s%c             %s, ", width, rs2);
            emitDispImmediate(offset, false, rs1Num);
            printf("(%s)\n", rs1);
            return;
        }
        case MajorOpcode::Branch:
        {
            unsigned opcode2 = (code >> 12) & 0x7;
            unsigned rs1     = (code >> 15) & 0x1f;
            unsigned rs2     = (code >> 20) & 0x1f;
            if (!emitDispBranch(opcode2, rs1, rs2, id, ig))
            {
                emitDispIllegalInstruction(code);
            }
            return;
        }
        case MajorOpcode::Load:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            unsigned     rs1Num  = (code >> 15) & 0x1f;
            const char*  rs1     = RegNames[rs1Num];
            const char*  rd      = RegNames[(code >> 7) & 0x1f];
            int          offset  = ((code >> 20) & 0xfff);
            if (offset & 0x800)
            {
                offset |= 0xfffff000;
            }

            char width  = "bhwd"[opcode2 & 0b011];
            char unsign = ((opcode2 & 0b100) != 0) ? 'u' : ' ';
            if (width == 'd' && unsign == 'u')
                return emitDispIllegalInstruction(code);

            printf("l%c%c            %s, ", width, unsign, rd);
            emitDispImmediate(offset, false, rs1Num);
            printf("(%s)\n", rs1);
            return;
        }
        case MajorOpcode::Jalr:
        {
            const unsigned rs1    = (code >> 15) & 0x1f;
            const unsigned rd     = (code >> 7) & 0x1f;
            int            offset = ((code >> 20) & 0xfff);
            if (offset & 0x800)
            {
                offset |= 0xfffff000;
            }

            if ((offset == 0) && (rs1 == REG_RA) && (rd == REG_ZERO))
            {
                printf("ret");
                return;
            }

            if ((offset == 0) && ((rd == REG_RA) || (rd == REG_ZERO)))
            {
                const char* name = (rd == REG_RA) ? "jalr" : "jr  ";
                printf("%s           %s", name, RegNames[rs1]);
            }
            else
            {
                printf("jalr           %s, ", RegNames[rd]);
                emitDispImmediate(offset, false);
                printf("(%s)", RegNames[rs1]);
            }
            CORINFO_METHOD_HANDLE handle = (CORINFO_METHOD_HANDLE)id->idDebugOnlyInfo()->idMemCookie;
            // Target for ret call is unclear, e.g.:
            //   jalr zero, 0(ra)
            // So, skip it
            if (handle != 0)
            {
                const char* methodName = emitComp->eeGetMethodFullName(handle);
                printf("\t\t// %s", methodName);
            }

            printf("\n");
            return;
        }
        case MajorOpcode::Jal:
        {
            unsigned rd = (code >> 7) & 0x1f;
            int offset  = (((code >> 31) & 0x1) << 20) | (((code >> 12) & 0xff) << 12) | (((code >> 20) & 0x1) << 11) |
                         (((code >> 21) & 0x3ff) << 1);
            if (offset & 0x80000)
            {
                offset |= 0xfff00000;
            }
            if ((rd == REG_ZERO) || (rd == REG_RA))
            {
                const char* name = (rd == REG_RA) ? "jal" : "j  ";
                printf("%s            ", name);

                if (id->idIsBound())
                {
                    emitPrintLabel(id->idAddr()->iiaIGlabel);
                }
                else
                {
                    printf("pc%+");
                    emitDispImmediate(offset / sizeof(code_t));
                    printf(" instructions");
                }
            }
            else
            {
                printf("jal            %s, ", RegNames[rd]);
                emitDispImmediate(offset, false);
            }
            CORINFO_METHOD_HANDLE handle = (CORINFO_METHOD_HANDLE)id->idDebugOnlyInfo()->idMemCookie;
            if (handle != 0)
            {
                const char* methodName = emitComp->eeGetMethodFullName(handle);
                printf("\t\t// %s", methodName);
            }

            printf("\n");
            return;
        }
        case MajorOpcode::MiscMem:
        {
            int pred = ((code) >> 24) & 0xf;
            int succ = ((code) >> 20) & 0xf;
            printf("fence          %d, %d\n", pred, succ);
            return;
        }
        case MajorOpcode::System:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            if (opcode2 != 0)
            {
                const char* rd      = RegNames[(code >> 7) & 0x1f];
                int         csrtype = (code >> 20);
                if (opcode2 <= 0x3)
                {
                    const char* rs1 = RegNames[(code >> 15) & 0x1f];
                    switch (opcode2)
                    {
                        case 0x1: // CSRRW
                            printf("csrrw           %s, %d, %s\n", rd, csrtype, rs1);
                            return;
                        case 0x2: // CSRRS
                            printf("csrrs           %s, %d, %s\n", rd, csrtype, rs1);
                            return;
                        case 0x3: // CSRRC
                            printf("csrrc           %s, %d, %s\n", rd, csrtype, rs1);
                            return;
                        default:
                            printf("RISCV64 illegal instruction: 0x%08X\n", code);
                            break;
                    }
                }
                else
                {
                    unsigned imm5 = ((code >> 15) & 0x1f);
                    switch (opcode2)
                    {
                        case 0x5: // CSRRWI
                            printf("csrrwi           %s, %d, %d\n", rd, csrtype, imm5);
                            return;
                        case 0x6: // CSRRSI
                            printf("csrrsi           %s, %d, %d\n", rd, csrtype, imm5);
                            return;
                        case 0x7: // CSRRCI
                            printf("csrrci           %s, %d, %d\n", rd, csrtype, imm5);
                            return;
                        default:
                            printf("RISCV64 illegal instruction: 0x%08X\n", code);
                            break;
                    }
                }
            }

            if (code == emitInsCode(INS_ebreak))
            {
                printf("ebreak\n");
            }
            else if (code == emitInsCode(INS_ecall))
            {
                printf("ecall\n");
            }
            else
            {
                NYI_RISCV64("illegal ins within emitDisInsName!");
            }
            return;
        }
        case MajorOpcode::OpFp:
        {
            unsigned int opcode2 = (code >> 25) & 0x7f;
            unsigned int opcode3 = (code >> 20) & 0x1f;
            unsigned int opcode4 = (code >> 12) & 0x7;
            const char*  fd      = RegNames[((code >> 7) & 0x1f) | 0x20];
            const char*  fs1     = RegNames[((code >> 15) & 0x1f) | 0x20];
            const char*  fs2     = RegNames[((code >> 20) & 0x1f) | 0x20];

            const char* xd  = RegNames[(code >> 7) & 0x1f];
            const char* xs1 = RegNames[(code >> 15) & 0x1f];
            const char* xs2 = RegNames[(code >> 20) & 0x1f];

            switch (opcode2)
            {
                case 0x00: // FADD.S
                    printf("fadd.s         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x04: // FSUB.S
                    printf("fsub.s         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x08: // FMUL.S
                    printf("fmul.s         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x0C: // FDIV.S
                    printf("fdiv.s         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x2C: // FSQRT.S
                    printf("fsqrt.s        %s, %s\n", fd, fs1);
                    return;
                case 0x10: // FSGNJ.S & FSGNJN.S & FSGNJX.S
                    NYI_IF(opcode4 >= 3, "RISC-V illegal fsgnj.s variant");
                    if (fs1 != fs2)
                    {
                        const char* variants[3] = {".s ", "n.s", "x.s"};
                        printf("fsgnj%s        %s, %s, %s\n", variants[opcode4], fd, fs1, fs2);
                    }
                    else // pseudos
                    {
                        const char* names[3] = {"fmv.s ", "fneg.s", "fabs.s"};
                        printf("%s         %s, %s\n", names[opcode4], fd, fs1);
                    }
                    return;
                case 0x14:            // FMIN.S & FMAX.S
                    if (opcode4 == 0) // FMIN.S
                    {
                        printf("fmin.s         %s, %s, %s\n", fd, fs1, fs2);
                    }
                    else if (opcode4 == 1) // FMAX.S
                    {
                        printf("fmax.s         %s, %s, %s\n", fd, fs1, fs2);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x60:            // FCVT.W.S & FCVT.WU.S & FCVT.L.S & FCVT.LU.S
                    if (opcode3 == 0) // FCVT.W.S
                    {
                        printf("fcvt.w.s       %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 1) // FCVT.WU.S
                    {
                        printf("fcvt.wu.s      %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 2) // FCVT.L.S
                    {
                        printf("fcvt.l.s       %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 3) // FCVT.LU.S
                    {
                        printf("fcvt.lu.s      %s, %s\n", xd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x70:            // FMV.X.W & FCLASS.S
                    if (opcode4 == 0) // FMV.X.W
                    {
                        printf("fmv.x.w        %s, %s\n", xd, fs1);
                    }
                    else if (opcode4 == 1) // FCLASS.S
                    {
                        printf("fclass.s       %s, %s\n", xd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x50:            // FLE.S & FLT.S & FEQ.S
                    if (opcode4 == 0) // FLE.S
                    {
                        printf("fle.s          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else if (opcode4 == 1) // FLT.S
                    {
                        printf("flt.s          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else if (opcode4 == 2) // FEQ.S
                    {
                        printf("feq.s          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x68:            // FCVT.S.W & FCVT.S.WU & FCVT.S.L & FCVT.S.LU
                    if (opcode3 == 0) // FCVT.S.W
                    {
                        printf("fcvt.s.w       %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 1) // FCVT.S.WU
                    {
                        printf("fcvt.s.wu      %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 2) // FCVT.S.L
                    {
                        printf("fcvt.s.l       %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 3) // FCVT.S.LU
                    {
                        printf("fcvt.s.lu      %s, %s\n", fd, xs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x78: // FMV.W.X
                    printf("fmv.w.x        %s, %s\n", fd, xs1);
                    return;
                case 0x1: // FADD.D
                    printf("fadd.d         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x5: // FSUB.D
                    printf("fsub.d         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x9: // FMUL.D
                    printf("fmul.d         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0xd: // FDIV.D
                    printf("fdiv.d         %s, %s, %s\n", fd, fs1, fs2);
                    return;
                case 0x2d: // FSQRT.D
                    printf("fsqrt.d        %s, %s\n", fd, fs1);
                    return;
                case 0x11: // FSGNJ.D & FSGNJN.D & FSGNJX.D
                    NYI_IF(opcode4 >= 3, "RISC-V illegal fsgnj.d variant");
                    if (fs1 != fs2)
                    {
                        const char* variants[3] = {".d ", "n.d", "x.d"};
                        printf("fsgnj%s        %s, %s, %s\n", variants[opcode4], fd, fs1, fs2);
                    }
                    else // pseudos
                    {
                        const char* names[3] = {"fmv.d ", "fneg.d", "fabs.d"};
                        printf("%s         %s, %s\n", names[opcode4], fd, fs1);
                    }
                    return;
                case 0x15:            // FMIN.D & FMAX.D
                    if (opcode4 == 0) // FMIN.D
                    {
                        printf("fmin.d         %s, %s, %s\n", fd, fs1, fs2);
                    }
                    else if (opcode4 == 1) // FMAX.D
                    {
                        printf("fmax.d         %s, %s, %s\n", fd, fs1, fs2);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x20:            // FCVT.S.D
                    if (opcode3 == 1) // FCVT.S.D
                    {
                        printf("fcvt.s.d       %s, %s\n", fd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x21:            // FCVT.D.S
                    if (opcode3 == 0) // FCVT.D.S
                    {
                        printf("fcvt.d.s       %s, %s\n", fd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x51:            // FLE.D & FLT.D & FEQ.D
                    if (opcode4 == 0) // FLE.D
                    {
                        printf("fle.d          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else if (opcode4 == 1) // FLT.D
                    {
                        printf("flt.d          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else if (opcode4 == 2) // FEQ.D
                    {
                        printf("feq.d          %s, %s, %s\n", xd, fs1, fs2);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x61: // FCVT.W.D & FCVT.WU.D & FCVT.L.D & FCVT.LU.D

                    if (opcode3 == 0) // FCVT.W.D
                    {
                        printf("fcvt.w.d       %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 1) // FCVT.WU.D
                    {
                        printf("fcvt.wu.d      %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 2) // FCVT.L.D
                    {
                        printf("fcvt.l.d       %s, %s\n", xd, fs1);
                    }
                    else if (opcode3 == 3) // FCVT.LU.D
                    {
                        printf("fcvt.lu.d      %s, %s\n", xd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x69:            // FCVT.D.W & FCVT.D.WU & FCVT.D.L & FCVT.D.LU
                    if (opcode3 == 0) // FCVT.D.W
                    {
                        printf("fcvt.d.w       %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 1) // FCVT.D.WU
                    {
                        printf("fcvt.d.wu      %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 2)
                    {
                        printf("fcvt.d.l       %s, %s\n", fd, xs1);
                    }
                    else if (opcode3 == 3)
                    {
                        printf("fcvt.d.lu      %s, %s\n", fd, xs1);
                    }

                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }

                    return;
                case 0x71:            // FMV.X.D & FCLASS.D
                    if (opcode4 == 0) // FMV.X.D
                    {
                        printf("fmv.x.d        %s, %s\n", xd, fs1);
                    }
                    else if (opcode4 == 1) // FCLASS.D
                    {
                        printf("fclass.d       %s, %s\n", xd, fs1);
                    }
                    else
                    {
                        NYI_RISCV64("illegal ins within emitDisInsName!");
                    }
                    return;
                case 0x79: // FMV.D.X
                    assert(opcode4 == 0);
                    printf("fmv.d.x        %s, %s\n", fd, xs1);
                    return;
                default:
                    NYI_RISCV64("illegal ins within emitDisInsName!");
                    return;
            }
            return;
        }
        case MajorOpcode::StoreFp:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            if ((opcode2 != 2) && (opcode2 != 3))
                return emitDispIllegalInstruction(code);

            unsigned    rs1Num = (code >> 15) & 0x1f;
            const char* rs1    = RegNames[rs1Num];
            const char* rs2    = RegNames[((code >> 20) & 0x1f) | 0x20];
            int         offset = (((code >> 25) & 0x7f) << 5) | ((code >> 7) & 0x1f);
            if (offset & 0x800)
            {
                offset |= 0xfffff000;
            }

            char width = "bhwd"[opcode2];
            printf("fs%c            %s, ", width, rs2);
            emitDispImmediate(offset, false, rs1Num);
            printf("(%s)\n", rs1);
            return;
        }
        case MajorOpcode::LoadFp:
        {
            unsigned int opcode2 = (code >> 12) & 0x7;
            if ((opcode2 != 2) && (opcode2 != 3))
                return emitDispIllegalInstruction(code);

            unsigned    rs1Num = (code >> 15) & 0x1f;
            const char* rs1    = RegNames[rs1Num];
            const char* rd     = RegNames[((code >> 7) & 0x1f) | 0x20];
            int         offset = ((code >> 20) & 0xfff);
            if (offset & 0x800)
            {
                offset |= 0xfffff000;
            }

            char width = "bhwd"[opcode2];
            printf("fl%c            %s, ", width, rd);
            emitDispImmediate(offset, false, rs1Num);
            printf("(%s)\n", rs1);
            return;
        }
        case MajorOpcode::Amo:
        {
            bool        hasDataReg = true;
            const char* name;
            switch (code >> 27) // funct5
            {
                case 0b00010:
                    name       = "lr";
                    hasDataReg = false;
                    break;
                case 0b00011:
                    name = "sc";
                    break;
                case 0b00001:
                    name = "amoswap";
                    break;
                case 0b00000:
                    name = "amoadd";
                    break;
                case 0b00100:
                    name = "amoxor";
                    break;
                case 0b01100:
                    name = "amoand";
                    break;
                case 0b01000:
                    name = "amoor";
                    break;
                case 0b10000:
                    name = "amomin";
                    break;
                case 0b10100:
                    name = "amomax";
                    break;
                case 0b11000:
                    name = "amominu";
                    break;
                case 0b11100:
                    name = "amomaxu";
                    break;
                default:
                    assert(!"Illegal funct5 within atomic memory operation, emitDisInsName");
                    name = "?";
            }

            char width;
            switch ((code >> 12) & 0x7) // funct3: width
            {
                case 0x2:
                    width = 'w';
                    break;
                case 0x3:
                    width = 'd';
                    break;
                default:
                    assert(!"Illegal width tag within atomic memory operation, emitDisInsName");
                    width = '?';
            }

            const char* aq = code & (1 << 25) ? "aq" : "";
            const char* rl = code & (1 << 26) ? "rl" : "";

            int len = printf("%s.%c.%s%s", name, width, aq, rl);
            if (len <= 0)
            {
                return;
            }
            static const int INS_LEN = 14;
            assert(len <= INS_LEN);

            const char* dest = RegNames[(code >> 7) & 0x1f];
            const char* addr = RegNames[(code >> 15) & 0x1f];

            int dataReg = (code >> 20) & 0x1f;
            if (hasDataReg)
            {
                const char* data = RegNames[dataReg];
                printf("%*s %s, %s, (%s)\n", INS_LEN - len, "", dest, data, addr);
            }
            else
            {
                assert(dataReg == REG_R0);
                printf("%*s %s, (%s)\n", INS_LEN - len, "", dest, addr);
            }
            return;
        }
        default:
            NO_WAY("illegal ins within emitDisInsName!");
    }

    NO_WAY("illegal ins within emitDisInsName!");
}

void emitter::emitDispInsInstrNum(const instrDesc* id) const
{
#ifdef DEBUG
    if (!emitComp->verbose)
        return;

    printf("IN%04x: ", id->idDebugOnlyInfo()->idNum);
#endif // DEBUG
}

void emitter::emitDispIns(
    instrDesc* id, bool isNew, bool doffs, bool asmfm, unsigned offset, BYTE* pCode, size_t sz, insGroup* ig)
{
    if (pCode == nullptr)
        return;

    emitDispInsInstrNum(id);

    bool willPrintLoadImmValue = (id->idInsOpt() == INS_OPTS_I) && !emitComp->opts.disDiffable;

    const BYTE* instr = pCode + writeableOffset;
    unsigned    instrSize;
    for (size_t i = 0; i < sz; instr += instrSize, i += instrSize, offset += instrSize)
    {
        // TODO-RISCV64: support different size instructions
        instrSize = sizeof(code_t);
        code_t instruction;
        memcpy(&instruction, instr, instrSize);
#ifdef DEBUG
        if (emitComp->verbose && i != 0)
        {
            printf("        ");
        }
#endif
        emitDispInsName(instruction, instr, doffs, offset, id, ig);

        if (willPrintLoadImmValue && ((i + instrSize) < sz))
        {
            printf("\n");
        }
    }

    if (willPrintLoadImmValue)
    {
        instrDescLoadImm* liid = static_cast<instrDescLoadImm*>(id);
        printf("\t\t;; load imm: hex=0x%016lX dec=%ld\n", liid->idcCnsVal, liid->idcCnsVal);
    }
}

#ifdef DEBUG

/*****************************************************************************
 *
 *  Display a stack frame reference.
 */

void emitter::emitDispFrameRef(int varx, int disp, int offs, bool asmfm)
{
    NYI_RISCV64("emitDispFrameRef-----unimplemented/unused on RISCV64 yet----");
}

#endif // DEBUG

// Generate code for a load or store operation with a potentially complex addressing mode
// This method handles the case of a GT_IND with contained GT_LEA op1 of the x86 form [base + index*sccale + offset]
//
void emitter::emitInsLoadStoreOp(instruction ins, emitAttr attr, regNumber dataReg, GenTreeIndir* indir)
{
    GenTree* addr = indir->Addr();

    if (addr->isContained())
    {
        assert(addr->OperIs(GT_LCL_ADDR, GT_LEA, GT_CNS_INT));

        int   offset = 0;
        DWORD lsl    = 0;

        if (addr->OperIs(GT_LEA))
        {
            offset = addr->AsAddrMode()->Offset();
            if (addr->AsAddrMode()->gtScale > 0)
            {
                assert(isPow2(addr->AsAddrMode()->gtScale));
                BitScanForward(&lsl, addr->AsAddrMode()->gtScale);
            }
        }

        GenTree* memBase = indir->Base();
        emitAttr addType = varTypeIsGC(memBase) ? EA_BYREF : EA_PTRSIZE;

        if (indir->HasIndex())
        {
            GenTree* index = indir->Index();

            if (offset != 0)
            {
                regNumber tmpReg = codeGen->internalRegisters.GetSingle(indir);

                if (isValidSimm12(offset))
                {
                    if (lsl > 0)
                    {
                        // Generate code to set tmpReg = base + index*scale
                        emitIns_R_R_I(INS_slli, addType, tmpReg, index->GetRegNum(), lsl);
                        emitIns_R_R_R(INS_add, addType, tmpReg, memBase->GetRegNum(), tmpReg);
                    }
                    else // no scale
                    {
                        // Generate code to set tmpReg = base + index
                        emitIns_R_R_R(INS_add, addType, tmpReg, memBase->GetRegNum(), index->GetRegNum());
                    }

                    noway_assert(emitInsIsLoad(ins) || (tmpReg != dataReg));

                    // Then load/store dataReg from/to [tmpReg + offset]
                    emitIns_R_R_I(ins, attr, dataReg, tmpReg, offset);
                }
                else // large offset
                {
                    // First load/store tmpReg with the large offset constant
                    emitLoadImmediate(EA_PTRSIZE, tmpReg,
                                      offset); // codeGen->instGen_Set_Reg_To_Imm(EA_PTRSIZE, tmpReg, offset);
                    // Then add the base register
                    //      rd = rd + base
                    emitIns_R_R_R(INS_add, addType, tmpReg, tmpReg, memBase->GetRegNum());

                    noway_assert(emitInsIsLoad(ins) || (tmpReg != dataReg));
                    noway_assert(tmpReg != index->GetRegNum());

                    regNumber scaleReg = codeGen->internalRegisters.GetSingle(indir);
                    // Then load/store dataReg from/to [tmpReg + index*scale]
                    emitIns_R_R_I(INS_slli, addType, scaleReg, index->GetRegNum(), lsl);
                    emitIns_R_R_R(INS_add, addType, tmpReg, tmpReg, scaleReg);
                    emitIns_R_R_I(ins, attr, dataReg, tmpReg, 0);
                }
            }
            else // (offset == 0)
            {
                // Then load/store dataReg from/to [memBase + index]
                switch (EA_SIZE(emitTypeSize(indir->TypeGet())))
                {
                    case EA_1BYTE:
                        assert(((ins <= INS_lhu) && (ins >= INS_lb)) || ins == INS_lwu || ins == INS_ld ||
                               ((ins <= INS_sw) && (ins >= INS_sb)) || ins == INS_sd);
                        if (ins <= INS_lhu || ins == INS_lwu || ins == INS_ld)
                        {
                            if (varTypeIsUnsigned(indir->TypeGet()))
                                ins = INS_lbu;
                            else
                                ins = INS_lb;
                        }
                        else
                            ins = INS_sb;
                        break;
                    case EA_2BYTE:
                        assert(((ins <= INS_lhu) && (ins >= INS_lb)) || ins == INS_lwu || ins == INS_ld ||
                               ((ins <= INS_sw) && (ins >= INS_sb)) || ins == INS_sd);
                        if (ins <= INS_lhu || ins == INS_lwu || ins == INS_ld)
                        {
                            if (varTypeIsUnsigned(indir->TypeGet()))
                                ins = INS_lhu;
                            else
                                ins = INS_lh;
                        }
                        else
                            ins = INS_sh;
                        break;
                    case EA_4BYTE:
                        assert(((ins <= INS_lhu) && (ins >= INS_lb)) || ins == INS_lwu || ins == INS_ld ||
                               ((ins <= INS_sw) && (ins >= INS_sb)) || ins == INS_sd || ins == INS_fsw ||
                               ins == INS_flw);
                        assert(INS_fsw > INS_sd);
                        if (ins <= INS_lhu || ins == INS_lwu || ins == INS_ld)
                        {
                            if (varTypeIsUnsigned(indir->TypeGet()))
                                ins = INS_lwu;
                            else
                                ins = INS_lw;
                        }
                        else if (ins != INS_flw && ins != INS_fsw)
                            ins = INS_sw;
                        break;
                    case EA_8BYTE:
                        assert(((ins <= INS_lhu) && (ins >= INS_lb)) || ins == INS_lwu || ins == INS_ld ||
                               ((ins <= INS_sw) && (ins >= INS_sb)) || ins == INS_sd || ins == INS_fld ||
                               ins == INS_fsd);
                        assert(INS_fsd > INS_sd);
                        if (ins <= INS_lhu || ins == INS_lwu || ins == INS_ld)
                        {
                            ins = INS_ld;
                        }
                        else if (ins != INS_fld && ins != INS_fsd)
                            ins = INS_sd;
                        break;
                    default:
                        NO_WAY("illegal ins within emitInsLoadStoreOp!");
                }

                if (lsl > 0)
                {
                    // Then load/store dataReg from/to [memBase + index*scale]
                    emitIns_R_R_I(INS_slli, emitActualTypeSize(index->TypeGet()), codeGen->rsGetRsvdReg(),
                                  index->GetRegNum(), lsl);
                    emitIns_R_R_R(INS_add, addType, codeGen->rsGetRsvdReg(), memBase->GetRegNum(),
                                  codeGen->rsGetRsvdReg());
                    emitIns_R_R_I(ins, attr, dataReg, codeGen->rsGetRsvdReg(), 0);
                }
                else // no scale
                {
                    emitIns_R_R_R(INS_add, addType, codeGen->rsGetRsvdReg(), memBase->GetRegNum(), index->GetRegNum());
                    emitIns_R_R_I(ins, attr, dataReg, codeGen->rsGetRsvdReg(), 0);
                }
            }
        }
        else // no Index register
        {
            if (addr->OperIs(GT_LCL_ADDR))
            {
                GenTreeLclVarCommon* varNode = addr->AsLclVarCommon();
                unsigned             lclNum  = varNode->GetLclNum();
                unsigned             offset  = varNode->GetLclOffs();
                if (emitInsIsStore(ins))
                {
                    emitIns_S_R(ins, attr, dataReg, lclNum, offset);
                }
                else
                {
                    emitIns_R_S(ins, attr, dataReg, lclNum, offset);
                }
            }
            else if (addr->OperIs(GT_CNS_INT))
            {
                assert(memBase == indir->Addr());
                ssize_t cns = addr->AsIntCon()->IconValue();

                ssize_t off = (cns << (64 - 12)) >> (64 - 12); // low 12 bits, sign-extended
                cns -= off;

                emitLoadImmediate(EA_PTRSIZE, codeGen->rsGetRsvdReg(), cns);
                emitIns_R_R_I(ins, attr, dataReg, codeGen->rsGetRsvdReg(), off);
            }
            else if (isValidSimm12(offset))
            {
                // Then load/store dataReg from/to [memBase + offset]
                emitIns_R_R_I(ins, attr, dataReg, memBase->GetRegNum(), offset);
            }
            else
            {
                // We require a tmpReg to hold the offset
                regNumber tmpReg = codeGen->internalRegisters.GetSingle(indir);

                // First load/store tmpReg with the large offset constant
                emitLoadImmediate(EA_PTRSIZE, tmpReg, offset);
                // codeGen->instGen_Set_Reg_To_Imm(EA_PTRSIZE, tmpReg, offset);

                // Then load/store dataReg from/to [memBase + tmpReg]
                emitIns_R_R_R(INS_add, addType, tmpReg, memBase->GetRegNum(), tmpReg);
                emitIns_R_R_I(ins, attr, dataReg, tmpReg, 0);
            }
        }
    }
    else // addr is not contained, so we evaluate it into a register
    {
#ifdef DEBUG
        if (addr->OperIs(GT_LCL_ADDR))
        {
            // If the local var is a gcref or byref, the local var better be untracked, because we have
            // no logic here to track local variable lifetime changes, like we do in the contained case
            // above. E.g., for a `st a0,[a1]` for byref `a1` to local `V01`, we won't store the local
            // `V01` and so the emitter can't update the GC lifetime for `V01` if this is a variable birth.
            LclVarDsc* varDsc = emitComp->lvaGetDesc(addr->AsLclVarCommon());
            assert(!varDsc->lvTracked);
        }
#endif // DEBUG

        // Then load/store dataReg from/to [addrReg]
        emitIns_R_R_I(ins, attr, dataReg, addr->GetRegNum(), 0);
    }
}

// The callee must call genConsumeReg() for any non-contained srcs
// and genProduceReg() for any non-contained dsts.

regNumber emitter::emitInsBinary(instruction ins, emitAttr attr, GenTree* dst, GenTree* src)
{
    NYI_RISCV64("emitInsBinary-----unimplemented/unused on RISCV64 yet----");
    return REG_R0;
}

// The callee must call genConsumeReg() for any non-contained srcs
// and genProduceReg() for any non-contained dsts.
regNumber emitter::emitInsTernary(instruction ins, emitAttr attr, GenTree* dst, GenTree* src1, GenTree* src2)
{
    // dst can only be a reg
    assert(!dst->isContained());

    // find immed (if any) - it cannot be a dst
    // Only one src can be an int.
    GenTreeIntConCommon* intConst  = nullptr;
    GenTree*             nonIntReg = nullptr;

    const bool needCheckOv = dst->gtOverflowEx();

    if (varTypeIsFloating(dst))
    {
        // src1 can only be a reg
        assert(!src1->isContained());
        // src2 can only be a reg
        assert(!src2->isContained());
    }
    else // not floating point
    {
        // src2 can be immed or reg
        assert(!src2->isContained() || src2->isContainedIntOrIImmed());

        // Check src2 first as we can always allow it to be a contained immediate
        if (src2->isContainedIntOrIImmed())
        {
            intConst  = src2->AsIntConCommon();
            nonIntReg = src1;
        }
        // Only for commutative operations do we check src1 and allow it to be a contained immediate
        else if (dst->OperIsCommutative())
        {
            // src1 can be immed or reg
            assert(!src1->isContained() || src1->isContainedIntOrIImmed());

            // Check src1 and allow it to be a contained immediate
            if (src1->isContainedIntOrIImmed())
            {
                assert(!src2->isContainedIntOrIImmed());
                intConst  = src1->AsIntConCommon();
                nonIntReg = src2;
            }
        }
        else
        {
            // src1 can only be a reg
            assert(!src1->isContained());
        }
    }

#ifdef DEBUG
    if (needCheckOv)
    {
        if (ins == INS_add)
        {
            assert(attr == EA_8BYTE);
        }
        else if (ins == INS_addw) // || ins == INS_add
        {
            assert(attr == EA_4BYTE);
        }
        else if (ins == INS_addi)
        {
            assert(intConst != nullptr);
        }
        else if (ins == INS_addiw)
        {
            assert(intConst != nullptr);
        }
        else if (ins == INS_sub)
        {
            assert(attr == EA_8BYTE);
        }
        else if (ins == INS_subw)
        {
            assert(attr == EA_4BYTE);
        }
        else if ((ins == INS_mul) || (ins == INS_mulh) || (ins == INS_mulhu))
        {
            assert(attr == EA_8BYTE);
            // NOTE: overflow format doesn't support an int constant operand directly.
            assert(intConst == nullptr);
        }
        else if (ins == INS_mulw)
        {
            assert(attr == EA_4BYTE);
            // NOTE: overflow format doesn't support an int constant operand directly.
            assert(intConst == nullptr);
        }
        else
        {
            printf("RISCV64-Invalid ins for overflow check: %s\n", codeGen->genInsName(ins));
            assert(!"Invalid ins for overflow check");
        }
    }
#endif // DEBUG

    regNumber dstReg  = dst->GetRegNum();
    regNumber src1Reg = src1->GetRegNum();
    regNumber src2Reg = src2->GetRegNum();

    if (intConst != nullptr)
    {
        ssize_t imm = intConst->IconValue();
        assert(isValidSimm12(imm));

        if (ins == INS_sub)
        {
            assert(attr == EA_8BYTE);
            assert(imm != -2048);
            ins = INS_addi;
            imm = -imm;
        }
        else if (ins == INS_subw)
        {
            assert(attr == EA_4BYTE);
            assert(imm != -2048);
            ins = INS_addiw;
            imm = -imm;
        }

        assert(ins == INS_addi || ins == INS_addiw || ins == INS_andi || ins == INS_ori || ins == INS_xori);

        regNumber tempReg = needCheckOv ? codeGen->internalRegisters.Extract(dst) : REG_NA;

        if (needCheckOv)
        {
            emitIns_R_R(INS_mov, attr, tempReg, nonIntReg->GetRegNum());
        }

        emitIns_R_R_I(ins, attr, dstReg, nonIntReg->GetRegNum(), imm);

        if (needCheckOv)
        {
            // At this point andi/ori/xori are excluded by previous checks
            assert(ins == INS_addi || ins == INS_addiw);

            // AS11 = B + C
            if ((dst->gtFlags & GTF_UNSIGNED) != 0)
            {
                codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, INS_bltu, dstReg, nullptr, tempReg);
            }
            else
            {
                if (imm > 0)
                {
                    // B > 0 and C > 0, if A < B, goto overflow
                    BasicBlock* tmpLabel = codeGen->genCreateTempLabel();
                    emitIns_J_cond_la(INS_bge, tmpLabel, REG_R0, tempReg);
                    emitIns_R_R_I(INS_slti, EA_PTRSIZE, tempReg, dstReg, imm);

                    codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, INS_bne, tempReg);

                    codeGen->genDefineTempLabel(tmpLabel);
                }
                else if (imm < 0)
                {
                    // B < 0 and C < 0, if A > B, goto overflow
                    BasicBlock* tmpLabel = codeGen->genCreateTempLabel();
                    emitIns_J_cond_la(INS_bge, tmpLabel, tempReg, REG_R0);
                    emitIns_R_R_I(INS_addi, attr, tempReg, REG_R0, imm);

                    codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, INS_blt, tempReg, nullptr, dstReg);

                    codeGen->genDefineTempLabel(tmpLabel);
                }
            }
        }
    }
    else if (varTypeIsFloating(dst))
    {
        emitIns_R_R_R(ins, attr, dstReg, src1Reg, src2Reg);
    }
    else
    {
        regNumber tempReg = needCheckOv ? codeGen->internalRegisters.Extract(dst) : REG_NA;

        switch (dst->OperGet())
        {
            case GT_MUL:
            {
                if (!needCheckOv && !(dst->gtFlags & GTF_UNSIGNED))
                {
                    emitIns_R_R_R(ins, attr, dstReg, src1Reg, src2Reg);
                }
                else
                {
                    if (needCheckOv)
                    {
                        assert(tempReg != dstReg);
                        assert(tempReg != src1Reg);
                        assert(tempReg != src2Reg);

                        assert(REG_RA != dstReg);
                        assert(REG_RA != src1Reg);
                        assert(REG_RA != src2Reg);

                        if ((dst->gtFlags & GTF_UNSIGNED) != 0)
                        {
                            if (attr == EA_4BYTE)
                            {
                                emitIns_R_R_I(INS_slli, EA_8BYTE, tempReg, src1Reg, 32);
                                emitIns_R_R_I(INS_slli, EA_8BYTE, REG_RA, src2Reg, 32);
                                emitIns_R_R_R(INS_mulhu, EA_8BYTE, tempReg, tempReg, REG_RA);
                                emitIns_R_R_I(INS_srai, attr, tempReg, tempReg, 32);
                            }
                            else
                            {
                                emitIns_R_R_R(INS_mulhu, attr, tempReg, src1Reg, src2Reg);
                            }
                        }
                        else
                        {
                            if (attr == EA_4BYTE)
                            {
                                emitIns_R_R_R(INS_mul, EA_8BYTE, tempReg, src1Reg, src2Reg);
                                emitIns_R_R_I(INS_srai, attr, tempReg, tempReg, 32);
                            }
                            else
                            {
                                emitIns_R_R_R(INS_mulh, attr, tempReg, src1Reg, src2Reg);
                            }
                        }
                    }

                    // n * n bytes will store n bytes result
                    emitIns_R_R_R(ins, attr, dstReg, src1Reg, src2Reg);

                    if ((dst->gtFlags & GTF_UNSIGNED) != 0)
                    {
                        if (attr == EA_4BYTE)
                        {
                            emitIns_R_R_I(INS_slli, EA_8BYTE, dstReg, dstReg, 32);
                            emitIns_R_R_I(INS_srli, EA_8BYTE, dstReg, dstReg, 32);
                        }
                    }

                    if (needCheckOv)
                    {
                        assert(tempReg != dstReg);
                        assert(tempReg != src1Reg);
                        assert(tempReg != src2Reg);

                        if ((dst->gtFlags & GTF_UNSIGNED) != 0)
                        {
                            codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, INS_bne, tempReg);
                        }
                        else
                        {
                            regNumber tempReg2 = codeGen->internalRegisters.Extract(dst);
                            assert(tempReg2 != dstReg);
                            assert(tempReg2 != src1Reg);
                            assert(tempReg2 != src2Reg);
                            size_t imm = (EA_SIZE(attr) == EA_8BYTE) ? 63 : 31;
                            emitIns_R_R_I(EA_SIZE(attr) == EA_8BYTE ? INS_srai : INS_sraiw, attr, tempReg2, dstReg,
                                          imm);
                            codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, INS_bne, tempReg, nullptr, tempReg2);
                        }
                    }
                }
            }
            break;

            case GT_AND:
            case GT_AND_NOT:
            case GT_OR:
            case GT_OR_NOT:
            case GT_XOR:
            case GT_XOR_NOT:
            {
                emitIns_R_R_R(ins, attr, dstReg, src1Reg, src2Reg);

                // TODO-RISCV64-CQ: here sign-extend dst when deal with 32bit data is too conservative.
                if (EA_SIZE(attr) == EA_4BYTE)
                    emitIns_R_R(INS_sext_w, attr, dstReg, dstReg);
            }
            break;

            case GT_ADD:
            case GT_SUB:
            {
                regNumber regOp1       = src1Reg;
                regNumber regOp2       = src2Reg;
                regNumber saveOperReg1 = REG_NA;
                regNumber saveOperReg2 = REG_NA;

                if ((dst->gtFlags & GTF_UNSIGNED) && (attr == EA_8BYTE))
                {
                    if (src1->TypeIs(TYP_INT))
                    {
                        emitIns_R_R_I(INS_slli, EA_8BYTE, regOp1, regOp1, 32);
                        emitIns_R_R_I(INS_srli, EA_8BYTE, regOp1, regOp1, 32);
                    }
                    if (src2->TypeIs(TYP_INT))
                    {
                        emitIns_R_R_I(INS_slli, EA_8BYTE, regOp2, regOp2, 32);
                        emitIns_R_R_I(INS_srli, EA_8BYTE, regOp2, regOp2, 32);
                    }
                }

                if (needCheckOv)
                {
                    assert(!varTypeIsFloating(dst));

                    assert(tempReg != dstReg);

                    if (dstReg == regOp1)
                    {
                        assert(tempReg != regOp1);
                        saveOperReg1 = tempReg;
                        saveOperReg2 = (regOp1 == regOp2) ? tempReg : regOp2;
                        emitIns_R_R(INS_mov, attr, tempReg, regOp1);
                    }
                    else if (dstReg == regOp2)
                    {
                        assert(tempReg != regOp2);
                        saveOperReg1 = regOp1;
                        saveOperReg2 = tempReg;
                        emitIns_R_R(INS_mov, attr, tempReg, regOp2);
                    }
                    else
                    {
                        saveOperReg1 = regOp1;
                        saveOperReg2 = regOp2;
                    }
                }

                emitIns_R_R_R(ins, attr, dstReg, regOp1, regOp2);

                /*
                    Check if A = B + C
                    ADD : A = B + C
                    SUB : B = A - C
                    In case of addition:
                    dst = src1 + src2
                    A = dst
                    B = src1
                    C = src2
                    In case of subtraction:
                    dst = src1 - src2
                    src1 = dst + src2
                    A = src1
                    B = dst
                    C = src2
                */
                if (needCheckOv)
                {
                    regNumber resultReg = REG_NA;

                    if (dst->OperIs(GT_ADD))
                    {
                        resultReg = dstReg;
                        regOp1    = saveOperReg1;
                        regOp2    = saveOperReg2;
                    }
                    else
                    {
                        resultReg = saveOperReg1;
                        regOp1    = dstReg;
                        regOp2    = saveOperReg2;
                    }

                    instruction branchIns  = INS_none;
                    regNumber   branchReg1 = REG_NA;
                    regNumber   branchReg2 = REG_NA;

                    if ((dst->gtFlags & GTF_UNSIGNED) != 0)
                    {
                        // if A < B then overflow
                        branchIns  = INS_bltu;
                        branchReg1 = resultReg;
                        branchReg2 = regOp1;
                    }
                    else
                    {
                        regNumber tempReg1 = codeGen->internalRegisters.GetSingle(dst);

                        branchIns = INS_bne;

                        if (attr == EA_4BYTE)
                        {
                            assert(!src1->TypeIs(TYP_LONG));
                            assert(!src2->TypeIs(TYP_LONG));

                            emitIns_R_R_R(INS_add, attr, tempReg1, regOp1, regOp2);

                            // if 64-bit addition is not equal to 32-bit addition for 32-bit operands then overflow
                            branchReg1 = resultReg;
                            branchReg2 = tempReg1;
                        }
                        else
                        {
                            assert(attr == EA_8BYTE);
                            assert(tempReg != tempReg1);
                            // When the tempReg2 is being used then the tempReg has to be already dead
                            regNumber tempReg2 = tempReg;

                            emitIns_R_R_R(INS_slt, attr, tempReg1, resultReg, regOp1);
                            emitIns_R_R_I(INS_slti, attr, tempReg2, regOp2, 0);

                            // if ((A < B) != (C < 0)) then overflow
                            branchReg1 = tempReg1;
                            branchReg2 = tempReg2;
                        }
                    }

                    codeGen->genJumpToThrowHlpBlk_la(SCK_OVERFLOW, branchIns, branchReg1, nullptr, branchReg2);
                }
            }
            break;

            default:
                NO_WAY("unexpected instruction within emitInsTernary!");
        }
    }

    return dstReg;
}

unsigned emitter::get_curTotalCodeSize()
{
    return emitTotalCodeSize;
}

#if defined(DEBUG) || defined(LATE_DISASM)

//----------------------------------------------------------------------------------------
// getInsExecutionCharacteristics:
//    Returns the current instruction execution characteristics based on the SiFive U74 core:
//    https://www.starfivetech.com/uploads/u74_core_complex_manual_21G1.pdf
//
// Arguments:
//    id  - The current instruction descriptor to be evaluated
//
// Return Value:
//    A struct containing the current instruction execution characteristics
//
// Notes:
//    The instruction latencies and throughput values returned by this function
//    are NOT accurate and just a function feature.
emitter::insExecutionCharacteristics emitter::getInsExecutionCharacteristics(instrDesc* id)
{
    insExecutionCharacteristics result;
    result.insThroughput       = PERFSCORE_LATENCY_1C;
    result.insLatency          = PERFSCORE_THROUGHPUT_1C;
    result.insMemoryAccessKind = PERFSCORE_MEMORY_NONE;

    unsigned codeSize = id->idCodeSize();
    assert((codeSize >= 4) && (codeSize % sizeof(code_t) == 0));

    // Some instructions like jumps or loads may have not-yet-known simple auxilliary instructions (lui, addi, slli,
    // etc) for building immediates, assume cost of one each.
    // instrDescLoadImm consists of OpImm, OpImm32, and Lui instructions.
    float immediateBuildingCost = ((codeSize / sizeof(code_t)) - 1) * PERFSCORE_LATENCY_1C;

    instruction ins = id->idIns();
    assert(ins != INS_invalid);
    if ((ins == INS_lea) || (id->idInsOpt() == INS_OPTS_I))
    {
        result.insLatency += immediateBuildingCost;
        result.insThroughput += immediateBuildingCost;
        return result;
    }

    MajorOpcode opcode = GetMajorOpcode(emitInsCode(ins));
    switch (opcode)
    {
        case MajorOpcode::OpImm:
        case MajorOpcode::OpImm32:
        case MajorOpcode::Lui:
        case MajorOpcode::Auipc:
            result.insLatency    = PERFSCORE_LATENCY_1C;
            result.insThroughput = PERFSCORE_THROUGHPUT_2X;
            break;

        case MajorOpcode::Op:
        case MajorOpcode::Op32:
            if (id->idInsIs(INS_mul, INS_mulh, INS_mulhu, INS_mulhsu, INS_mulw))
            {
                result.insLatency = PERFSCORE_LATENCY_3C;
            }
            else if (id->idInsIs(INS_div, INS_divu, INS_rem, INS_remu))
            {
                result.insLatency = result.insThroughput = (6.0f + 68.0f) / 2;
            }
            else if (id->idInsIs(INS_divw, INS_divuw, INS_remw, INS_remuw))
            {
                result.insLatency = result.insThroughput = (6.0f + 36.0f) / 2;
            }
            else
            {
                result.insThroughput = PERFSCORE_THROUGHPUT_2X;
            }
            break;

        case MajorOpcode::MAdd:
        case MajorOpcode::MSub:
        case MajorOpcode::NmAdd:
        case MajorOpcode::NmSub:
        case MajorOpcode::OpFp:
            if (id->idInsIs(INS_fadd_s, INS_fsub_s, INS_fmul_s, INS_fmadd_s, INS_fmsub_s, INS_fnmadd_s, INS_fnmsub_s))
            {
                result.insLatency = PERFSCORE_LATENCY_5C;
            }
            else if (id->idInsIs(INS_fadd_d, INS_fsub_d, INS_fmul_d, INS_fmadd_d, INS_fmsub_d, INS_fnmadd_d,
                                 INS_fnmsub_d))
            {
                result.insLatency = PERFSCORE_LATENCY_7C;
            }
            else if (id->idInsIs(INS_fdiv_s))
            {
                result.insLatency    = (9.0f + 36.0f) / 2;
                result.insThroughput = (8.0f + 33.0f) / 2;
            }
            else if (id->idInsIs(INS_fsqrt_s))
            {
                result.insLatency    = (9.0f + 28.0f) / 2;
                result.insThroughput = (8.0f + 33.0f) / 2;
            }
            else if (id->idInsIs(INS_fdiv_d))
            {
                result.insLatency    = (9.0f + 58.0f) / 2;
                result.insThroughput = (8.0f + 58.0f) / 2;
            }
            else if (id->idInsIs(INS_fsqrt_d))
            {
                result.insLatency    = (9.0f + 57.0f) / 2;
                result.insThroughput = (8.0f + 58.0f) / 2;
            }
            else if (id->idInsIs(INS_feq_s, INS_fle_s, INS_flt_s, INS_fclass_s, INS_feq_d, INS_fle_d, INS_flt_d,
                                 INS_fclass_d, INS_fcvt_w_s, INS_fcvt_l_s, INS_fcvt_s_l, INS_fcvt_wu_s, INS_fcvt_lu_s,
                                 INS_fcvt_s_lu, INS_fcvt_w_d, INS_fcvt_l_d, INS_fcvt_wu_d, INS_fcvt_lu_d))
            {
                result.insLatency = PERFSCORE_LATENCY_4C;
            }
            else if (id->idInsIs(INS_fcvt_d_l, INS_fcvt_d_lu, INS_fmv_d_x))
            {
                result.insLatency = PERFSCORE_LATENCY_6C;
            }
            else if (id->idInsIs(INS_fmv_x_w, INS_fmv_x_d))
            {
                result.insLatency = PERFSCORE_LATENCY_1C;
            }
            else
            {
                result.insLatency = PERFSCORE_LATENCY_2C;
            }
            break;

        case MajorOpcode::Amo:
            result.insLatency = result.insThroughput = PERFSCORE_LATENCY_5C;
            result.insMemoryAccessKind               = PERFSCORE_MEMORY_READ_WRITE;
            break;

        case MajorOpcode::Branch:
            result.insLatency = result.insThroughput =
                immediateBuildingCost + (PERFSCORE_LATENCY_1C + PERFSCORE_LATENCY_6C) / 2;
            break;

        case MajorOpcode::Jalr:
            result.insLatency = result.insThroughput =
                immediateBuildingCost + (PERFSCORE_LATENCY_1C + PERFSCORE_LATENCY_5C) / 2;
            break;

        case MajorOpcode::Jal:
            result.insLatency = result.insThroughput =
                immediateBuildingCost + (PERFSCORE_LATENCY_1C + PERFSCORE_LATENCY_2C) / 2;
            break;

        case MajorOpcode::System:
        {
            code_t code   = id->idAddr()->iiaGetInstrEncode();
            code_t funct3 = (code >> 12) & 0b111;
            if (funct3 != 0)
            {
                bool isCsrrw      = ((funct3 & 0b11) == 0b01);
                bool isZero       = (((code >> 15) & 0b11111) == 0); // source register or 5-bit immediate is zero
                bool isWrite      = (isCsrrw || !isZero);
                result.insLatency = isWrite ? PERFSCORE_LATENCY_7C : PERFSCORE_LATENCY_1C;
            }
            break;
        }

        case MajorOpcode::Load:
        case MajorOpcode::Store:
        case MajorOpcode::LoadFp:
        case MajorOpcode::StoreFp:
        {
            bool isLoad = (opcode == MajorOpcode::Load || opcode == MajorOpcode::LoadFp);

            result.insLatency = isLoad ? PERFSCORE_LATENCY_2C : PERFSCORE_LATENCY_4C;
            if (isLoad)
            {
                code_t log2Size = (emitInsCode(ins) >> 12) & 0b11;
                if (log2Size < 2) // sub-word loads
                    result.insLatency += PERFSCORE_LATENCY_1C;
            }

            regNumber baseReg = id->idReg2();
            if (baseReg != REG_SP || baseReg != REG_FP)
                result.insLatency += PERFSCORE_LATENCY_1C; // assume non-stack load/stores are more likely to cache-miss

            result.insThroughput += immediateBuildingCost;
            result.insMemoryAccessKind = isLoad ? PERFSCORE_MEMORY_READ : PERFSCORE_MEMORY_WRITE;
            break;
        }

        case MajorOpcode::MiscMem:
            result.insLatency    = PERFSCORE_LATENCY_5C;
            result.insThroughput = PERFSCORE_THROUGHPUT_5C;
            break;

        default:
            perfScoreUnhandledInstruction(id, &result);
    }

    return result;
}

#endif // defined(DEBUG) || defined(LATE_DISASM)

#ifdef DEBUG
//------------------------------------------------------------------------
// emitRegName: Returns a general-purpose register name or SIMD and floating-point scalar register name.
//
// TODO-RISCV64: supporting SIMD.
// Arguments:
//    reg - A general-purpose register orfloating-point register.
//    size - unused parameter.
//    varName - unused parameter.
//
// Return value:
//    A string that represents a general-purpose register name or floating-point scalar register name.
//
const char* emitter::emitRegName(regNumber reg, emitAttr size, bool varName) const
{
    assert(reg < REG_COUNT);

    const char* rn = nullptr;

    rn = RegNames[reg];
    assert(rn != nullptr);

    return rn;
}
#endif

#endif // defined(TARGET_RISCV64)
