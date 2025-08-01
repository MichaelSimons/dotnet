// Copyright (c) Microsoft Corporation. All Rights Reserved. See License.txt in the project root for license information.

/// Defines derived expression manipulation and construction functions.
module internal FSharp.Compiler.TypedTreeOps

open System
open System.CodeDom.Compiler
open System.Collections.Generic
open System.Collections.Immutable
open Internal.Utilities
open Internal.Utilities.Collections
open Internal.Utilities.Library
open Internal.Utilities.Library.Extras
open Internal.Utilities.Rational

open FSharp.Compiler.IO
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.Syntax
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.SyntaxTreeOps
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.Text.Layout
open FSharp.Compiler.Text.LayoutRender
open FSharp.Compiler.Text.TaggedText
open FSharp.Compiler.Xml
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeBasics
#if !NO_TYPEPROVIDERS
open FSharp.Compiler.TypeProviders
#endif

let AccFreeVarsStackGuardDepth = GetEnvInteger "FSHARP_AccFreeVars" 100
let RemapExprStackGuardDepth = GetEnvInteger "FSHARP_RemapExpr" 50
let FoldExprStackGuardDepth = GetEnvInteger "FSHARP_FoldExpr" 50

let inline compareBy (x: 'T MaybeNull) (y: 'T MaybeNull) ([<InlineIfLambda>]func: 'T -> 'K)  = 
    match x,y with
    | null,null -> 0
    | null,_ -> -1
    | _,null -> 1
    | x,y ->  compare (func !!x) (func !!y)

//---------------------------------------------------------------------------
// Basic data structures
//---------------------------------------------------------------------------

[<NoEquality; NoComparison>]
type TyparMap<'T> = 
    | TPMap of StampMap<'T>

    member tm.Item 
        with get (tp: Typar) = 
            let (TPMap m) = tm
            m[tp.Stamp]

    member tm.ContainsKey (tp: Typar) = 
        let (TPMap m) = tm
        m.ContainsKey(tp.Stamp)
        
    member tm.TryGetValue (tp: Typar) = 
        let (TPMap m) = tm
        m.TryGetValue(tp.Stamp)

    member tm.TryFind (tp: Typar) = 
        let (TPMap m) = tm
        m.TryFind(tp.Stamp)

    member tm.Add (tp: Typar, x) = 
        let (TPMap m) = tm
        TPMap (m.Add(tp.Stamp, x))

    static member Empty: TyparMap<'T> = TPMap Map.empty

[<NoEquality; NoComparison; Sealed>]
type TyconRefMap<'T>(imap: StampMap<'T>) =
    member _.Item with get (tcref: TyconRef) = imap[tcref.Stamp]
    member _.TryFind (tcref: TyconRef) = imap.TryFind tcref.Stamp 
    member _.ContainsKey (tcref: TyconRef) = imap.ContainsKey tcref.Stamp 
    member _.Add (tcref: TyconRef) x = TyconRefMap (imap.Add (tcref.Stamp, x))
    member _.Remove (tcref: TyconRef) = TyconRefMap (imap.Remove tcref.Stamp)
    member _.IsEmpty = imap.IsEmpty
    member _.TryGetValue (tcref: TyconRef) = imap.TryGetValue tcref.Stamp 

    static member Empty: TyconRefMap<'T> = TyconRefMap Map.empty
    static member OfList vs = (vs, TyconRefMap<'T>.Empty) ||> List.foldBack (fun (x, y) acc -> acc.Add x y) 

[<Struct>]
[<NoEquality; NoComparison>]
type ValMap<'T>(imap: StampMap<'T>) = 
     
    member _.Contents = imap
    member _.Item with get (v: Val) = imap[v.Stamp]
    member _.TryFind (v: Val) = imap.TryFind v.Stamp 
    member _.ContainsVal (v: Val) = imap.ContainsKey v.Stamp 
    member _.Add (v: Val) x = ValMap (imap.Add(v.Stamp, x))
    member _.Remove (v: Val) = ValMap (imap.Remove(v.Stamp))
    static member Empty = ValMap<'T> Map.empty
    member _.IsEmpty = imap.IsEmpty
    static member OfList vs = (vs, ValMap<'T>.Empty) ||> List.foldBack (fun (x, y) acc -> acc.Add x y) 

//--------------------------------------------------------------------------
// renamings
//--------------------------------------------------------------------------

type TyparInstantiation = (Typar * TType) list

type TyconRefRemap = TyconRefMap<TyconRef>
type ValRemap = ValMap<ValRef>

let emptyTyconRefRemap: TyconRefRemap = TyconRefMap<_>.Empty
let emptyTyparInst = ([]: TyparInstantiation)

[<NoEquality; NoComparison>]
type Remap =
    { tpinst: TyparInstantiation

      /// Values to remap
      valRemap: ValRemap

      /// TyconRefs to remap
      tyconRefRemap: TyconRefRemap

      /// Remove existing trait solutions?
      removeTraitSolutions: bool }

let emptyRemap = 
    { tpinst = emptyTyparInst
      tyconRefRemap = emptyTyconRefRemap
      valRemap = ValMap.Empty
      removeTraitSolutions = false }

type Remap with 
    static member Empty = emptyRemap

//--------------------------------------------------------------------------
// Substitute for type variables and remap type constructors 
//--------------------------------------------------------------------------

let addTyconRefRemap tcref1 tcref2 tmenv = 
    { tmenv with tyconRefRemap = tmenv.tyconRefRemap.Add tcref1 tcref2 }

let isRemapEmpty remap = 
    isNil remap.tpinst && 
    remap.tyconRefRemap.IsEmpty && 
    remap.valRemap.IsEmpty 

let rec instTyparRef tpinst ty tp =
    match tpinst with 
    | [] -> ty
    | (tpR, tyR) :: t -> 
        if typarEq tp tpR then tyR 
        else instTyparRef t ty tp

let remapTyconRef (tcmap: TyconRefMap<_>) tcref =
    match tcmap.TryFind tcref with 
    | Some tcref -> tcref
    | None -> tcref

let remapUnionCaseRef tcmap (UnionCaseRef(tcref, nm)) = UnionCaseRef(remapTyconRef tcmap tcref, nm)
let remapRecdFieldRef tcmap (RecdFieldRef(tcref, nm)) = RecdFieldRef(remapTyconRef tcmap tcref, nm)

let mkTyparInst (typars: Typars) tyargs =
    (List.zip typars tyargs: TyparInstantiation)

let generalizeTypar tp = mkTyparTy tp
let generalizeTypars tps = List.map generalizeTypar tps

let rec remapTypeAux (tyenv: Remap) (ty: TType) =
  let ty = stripTyparEqns ty
  match ty with
  | TType_var (tp, nullness) as ty -> 
      let res = instTyparRef tyenv.tpinst ty tp
      addNullnessToTy nullness res

  | TType_app (tcref, tinst, flags) as ty -> 
      match tyenv.tyconRefRemap.TryFind tcref with 
      | Some tcrefR -> TType_app (tcrefR, remapTypesAux tyenv tinst, flags)
      | None -> 
          match tinst with 
          | [] -> ty  // optimization to avoid re-allocation of TType_app node in the common case 
          | _ -> 
              // avoid reallocation on idempotent 
              let tinstR = remapTypesAux tyenv tinst
              if tinst === tinstR then ty else 
              TType_app (tcref, tinstR, flags)

  | TType_ucase (UnionCaseRef(tcref, n), tinst) -> 
      match tyenv.tyconRefRemap.TryFind tcref with 
      | Some tcrefR -> TType_ucase (UnionCaseRef(tcrefR, n), remapTypesAux tyenv tinst)
      | None -> TType_ucase (UnionCaseRef(tcref, n), remapTypesAux tyenv tinst)

  | TType_anon (anonInfo, l) as ty -> 
      let tupInfoR = remapTupInfoAux tyenv anonInfo.TupInfo
      let lR = remapTypesAux tyenv l
      if anonInfo.TupInfo === tupInfoR && l === lR then ty else  
      TType_anon (AnonRecdTypeInfo.Create(anonInfo.Assembly, tupInfoR, anonInfo.SortedIds), lR)

  | TType_tuple (tupInfo, l) as ty -> 
      let tupInfoR = remapTupInfoAux tyenv tupInfo
      let lR = remapTypesAux tyenv l
      if tupInfo === tupInfoR && l === lR then ty else  
      TType_tuple (tupInfoR, lR)

  | TType_fun (domainTy, rangeTy, flags) as ty -> 
      let domainTyR = remapTypeAux tyenv domainTy
      let retTyR = remapTypeAux tyenv rangeTy
      if domainTy === domainTyR && rangeTy === retTyR then ty else
      TType_fun (domainTyR, retTyR, flags)

  | TType_forall (tps, ty) -> 
      let tpsR, tyenv = copyAndRemapAndBindTypars tyenv tps
      TType_forall (tpsR, remapTypeAux tyenv ty)

  | TType_measure unt -> 
      TType_measure (remapMeasureAux tyenv unt)


and remapMeasureAux tyenv unt =
    match unt with
    | Measure.One _ -> unt
    | Measure.Const(entityRef, m) ->
        match tyenv.tyconRefRemap.TryFind entityRef with 
        | Some tcref -> Measure.Const(tcref, m)
        | None -> unt
    | Measure.Prod(u1, u2, m) -> Measure.Prod(remapMeasureAux tyenv u1, remapMeasureAux tyenv u2, m)
    | Measure.RationalPower(u, q) -> Measure.RationalPower(remapMeasureAux tyenv u, q)
    | Measure.Inv u -> Measure.Inv(remapMeasureAux tyenv u)
    | Measure.Var tp as unt -> 
       match tp.Solution with
       | None -> 
          match ListAssoc.tryFind typarEq tp tyenv.tpinst with
          | Some tpTy -> 
              match tpTy with
              | TType_measure unt -> unt
              | TType_var(typar= typar) when tp.Kind = TyparKind.Measure ->
                    // This is a measure typar that is not yet solved, so we can't remap it
                    error(Error(FSComp.SR.tcExpectedTypeParamMarkedWithUnitOfMeasureAttribute(), typar.Range))
              | _ -> failwith "remapMeasureAux: incorrect kinds"
          | None -> unt
       | Some (TType_measure unt) -> remapMeasureAux tyenv unt
       | Some ty -> failwithf "incorrect kinds: %A" ty

and remapTupInfoAux _tyenv unt =
    match unt with
    | TupInfo.Const _ -> unt

and remapTypesAux tyenv types = List.mapq (remapTypeAux tyenv) types
and remapTyparConstraintsAux tyenv cs =
   cs |> List.choose (fun x -> 
         match x with 
         | TyparConstraint.CoercesTo(ty, m) -> 
             Some(TyparConstraint.CoercesTo (remapTypeAux tyenv ty, m))
         | TyparConstraint.MayResolveMember(traitInfo, m) -> 
             Some(TyparConstraint.MayResolveMember (remapTraitInfo tyenv traitInfo, m))
         | TyparConstraint.DefaultsTo(priority, ty, m) ->
             Some(TyparConstraint.DefaultsTo(priority, remapTypeAux tyenv ty, m))
         | TyparConstraint.IsEnum(underlyingTy, m) -> 
             Some(TyparConstraint.IsEnum(remapTypeAux tyenv underlyingTy, m))
         | TyparConstraint.IsDelegate(argTys, retTy, m) -> 
             Some(TyparConstraint.IsDelegate(remapTypeAux tyenv argTys, remapTypeAux tyenv retTy, m))
         | TyparConstraint.SimpleChoice(tys, m) ->
             Some(TyparConstraint.SimpleChoice(remapTypesAux tyenv tys, m))
         | TyparConstraint.SupportsComparison _ 
         | TyparConstraint.SupportsEquality _ 
         | TyparConstraint.SupportsNull _ 
         | TyparConstraint.NotSupportsNull _ 
         | TyparConstraint.IsUnmanaged _
         | TyparConstraint.AllowsRefStruct _
         | TyparConstraint.IsNonNullableStruct _ 
         | TyparConstraint.IsReferenceType _ 
         | TyparConstraint.RequiresDefaultConstructor _ -> Some x)

and remapTraitInfo tyenv (TTrait(tys, nm, flags, argTys, retTy, source, slnCell)) =
    let slnCell = 
        match slnCell.Value with 
        | None -> None
        | _ when tyenv.removeTraitSolutions -> None
        | Some sln -> 
            let sln = 
                match sln with 
                | ILMethSln(ty, extOpt, ilMethRef, minst, staticTyOpt) ->
                     ILMethSln(remapTypeAux tyenv ty, extOpt, ilMethRef, remapTypesAux tyenv minst, Option.map (remapTypeAux tyenv) staticTyOpt)  
                | FSMethSln(ty, vref, minst, staticTyOpt) ->
                     FSMethSln(remapTypeAux tyenv ty, remapValRef tyenv vref, remapTypesAux tyenv minst, Option.map (remapTypeAux tyenv) staticTyOpt)  
                | FSRecdFieldSln(tinst, rfref, isSet) ->
                     FSRecdFieldSln(remapTypesAux tyenv tinst, remapRecdFieldRef tyenv.tyconRefRemap rfref, isSet)  
                | FSAnonRecdFieldSln(anonInfo, tinst, n) ->
                     FSAnonRecdFieldSln(anonInfo, remapTypesAux tyenv tinst, n)  
                | BuiltInSln -> 
                     BuiltInSln
                | ClosedExprSln e -> 
                     ClosedExprSln e // no need to remap because it is a closed expression, referring only to external types
            Some sln
    
    let tysR = remapTypesAux tyenv tys
    let argTysR = remapTypesAux tyenv argTys
    let retTyR = Option.map (remapTypeAux tyenv) retTy

    // Note: we reallocate a new solution cell on every traversal of a trait constraint
    // This feels incorrect for trait constraints that are quantified: it seems we should have 
    // formal binders for trait constraints when they are quantified, just as
    // we have formal binders for type variables.
    //
    // The danger here is that a solution for one syntactic occurrence of a trait constraint won't
    // be propagated to other, "linked" solutions. However trait constraints don't appear in any algebra
    // in the same way as types
    let newSlnCell = ref slnCell

    TTrait(tysR, nm, flags, argTysR, retTyR, source, newSlnCell)

and bindTypars tps tyargs tpinst =   
    match tps with 
    | [] -> tpinst 
    | _ -> List.map2 (fun tp tyarg -> (tp, tyarg)) tps tyargs @ tpinst 

// This version is used to remap most type parameters, e.g. ones bound at tycons, vals, records 
// See notes below on remapTypeFull for why we have a function that accepts remapAttribs as an argument 
and copyAndRemapAndBindTyparsFull remapAttrib tyenv tps =
    match tps with 
    | [] -> tps, tyenv 
    | _ -> 
      let tpsR = copyTypars false tps
      let tyenv = { tyenv with tpinst = bindTypars tps (generalizeTypars tpsR) tyenv.tpinst } 
      (tps, tpsR) ||> List.iter2 (fun tporig tp -> 
         tp.SetConstraints (remapTyparConstraintsAux tyenv tporig.Constraints)
         tp.SetAttribs (tporig.Attribs |> remapAttrib))
      tpsR, tyenv

// copies bound typars, extends tpinst 
and copyAndRemapAndBindTypars tyenv tps =
    copyAndRemapAndBindTyparsFull (fun _ -> []) tyenv tps

and remapValLinkage tyenv (vlink: ValLinkageFullKey) = 
    let tyOpt = vlink.TypeForLinkage
    let tyOptR = 
        match tyOpt with 
        | None -> tyOpt 
        | Some ty -> 
            let tyR = remapTypeAux tyenv ty
            if ty === tyR then tyOpt else
            Some tyR
    if tyOpt === tyOptR then vlink else
    ValLinkageFullKey(vlink.PartialKey, tyOptR)

and remapNonLocalValRef tyenv (nlvref: NonLocalValOrMemberRef) = 
    let eref = nlvref.EnclosingEntity
    let erefR = remapTyconRef tyenv.tyconRefRemap eref
    let vlink = nlvref.ItemKey
    let vlinkR = remapValLinkage tyenv vlink
    if eref === erefR && vlink === vlinkR then nlvref else
    { EnclosingEntity = erefR
      ItemKey = vlinkR }

and remapValRef tmenv (vref: ValRef) = 
    match tmenv.valRemap.TryFind vref.Deref with 
    | None -> 
        if vref.IsLocalRef then vref else 
        let nlvref = vref.nlr
        let nlvrefR = remapNonLocalValRef tmenv nlvref
        if nlvref === nlvrefR then vref else
        VRefNonLocal nlvrefR
    | Some res -> 
        res

let remapType tyenv x =
    if isRemapEmpty tyenv then x else
    remapTypeAux tyenv x

let remapTypes tyenv x = 
    if isRemapEmpty tyenv then x else 
    remapTypesAux tyenv x

/// Use this one for any type that may be a forall type where the type variables may contain attributes 
/// Logically speaking this is mutually recursive with remapAttribImpl defined much later in this file, 
/// because types may contain forall types that contain attributes, which need to be remapped. 
/// We currently break the recursion by passing in remapAttribImpl as a function parameter. 
/// Use this one for any type that may be a forall type where the type variables may contain attributes 
let remapTypeFull remapAttrib tyenv ty =
    if isRemapEmpty tyenv then ty else 
    match stripTyparEqns ty with
    | TType_forall(tps, tau) -> 
        let tpsR, tyenvinner = copyAndRemapAndBindTyparsFull remapAttrib tyenv tps
        TType_forall(tpsR, remapType tyenvinner tau)
    | _ -> 
        remapType tyenv ty

let remapParam tyenv (TSlotParam(nm, ty, fl1, fl2, fl3, attribs) as x) = 
    if isRemapEmpty tyenv then x else 
    TSlotParam(nm, remapTypeAux tyenv ty, fl1, fl2, fl3, attribs) 

let remapSlotSig remapAttrib tyenv (TSlotSig(nm, ty, ctps, methTypars, paraml, retTy) as x) =
    if isRemapEmpty tyenv then x else 
    let tyR = remapTypeAux tyenv ty
    let ctpsR, tyenvinner = copyAndRemapAndBindTyparsFull remapAttrib tyenv ctps
    let methTyparsR, tyenvinner = copyAndRemapAndBindTyparsFull remapAttrib tyenvinner methTypars
    TSlotSig(nm, tyR, ctpsR, methTyparsR, List.mapSquared (remapParam tyenvinner) paraml, Option.map (remapTypeAux tyenvinner) retTy) 

let mkInstRemap tpinst = 
    { tyconRefRemap = emptyTyconRefRemap
      tpinst = tpinst
      valRemap = ValMap.Empty
      removeTraitSolutions = false }

// entry points for "typar -> TType" instantiation 
let instType tpinst x = if isNil tpinst then x else remapTypeAux (mkInstRemap tpinst) x
let instTypes tpinst x = if isNil tpinst then x else remapTypesAux (mkInstRemap tpinst) x
let instTrait tpinst x = if isNil tpinst then x else remapTraitInfo (mkInstRemap tpinst) x
let instTyparConstraints tpinst x = if isNil tpinst then x else remapTyparConstraintsAux (mkInstRemap tpinst) x
let instSlotSig tpinst ss = remapSlotSig (fun _ -> []) (mkInstRemap tpinst) ss
let copySlotSig ss = remapSlotSig (fun _ -> []) Remap.Empty ss


let mkTyparToTyparRenaming tpsorig tps = 
    let tinst = generalizeTypars tps
    mkTyparInst tpsorig tinst, tinst

let mkTyconInst (tycon: Tycon) tinst = mkTyparInst tycon.TyparsNoRange tinst
let mkTyconRefInst (tcref: TyconRef) tinst = mkTyconInst tcref.Deref tinst

//---------------------------------------------------------------------------
// Basic equalities
//---------------------------------------------------------------------------

let tyconRefEq (g: TcGlobals) tcref1 tcref2 = primEntityRefEq g.compilingFSharpCore g.fslibCcu tcref1 tcref2
let valRefEq (g: TcGlobals) vref1 vref2 = primValRefEq g.compilingFSharpCore g.fslibCcu vref1 vref2

//---------------------------------------------------------------------------
// Remove inference equations and abbreviations from units
//---------------------------------------------------------------------------

let reduceTyconRefAbbrevMeasureable (tcref: TyconRef) = 
    let abbrev = tcref.TypeAbbrev
    match abbrev with 
    | Some (TType_measure ms) -> ms
    | _ -> invalidArg "tcref" "not a measure abbreviation, or incorrect kind"

let rec stripUnitEqnsFromMeasureAux canShortcut unt = 
    match stripUnitEqnsAux canShortcut unt with 
    | Measure.Const(tyconRef= tcref) when tcref.IsTypeAbbrev ->  
        stripUnitEqnsFromMeasureAux canShortcut (reduceTyconRefAbbrevMeasureable tcref) 
    | m -> m

let stripUnitEqnsFromMeasure m = stripUnitEqnsFromMeasureAux false m

//---------------------------------------------------------------------------
// Basic unit stuff
//---------------------------------------------------------------------------

/// What is the contribution of unit-of-measure constant ucref to unit-of-measure expression measure? 
let rec MeasureExprConExponent g abbrev ucref unt =
    match (if abbrev then stripUnitEqnsFromMeasure unt else stripUnitEqns unt) with
    | Measure.Const(tyconRef= ucrefR) -> if tyconRefEq g ucrefR ucref then OneRational else ZeroRational
    | Measure.Inv untR -> NegRational(MeasureExprConExponent g abbrev ucref untR)
    | Measure.Prod(measure1= unt1; measure2= unt2) -> AddRational(MeasureExprConExponent g abbrev ucref unt1) (MeasureExprConExponent g abbrev ucref unt2)
    | Measure.RationalPower(measure= untR; power= q) -> MulRational (MeasureExprConExponent g abbrev ucref untR) q
    | _ -> ZeroRational

/// What is the contribution of unit-of-measure constant ucref to unit-of-measure expression measure
/// after remapping tycons? 
let rec MeasureConExponentAfterRemapping g r ucref unt =
    match stripUnitEqnsFromMeasure unt with
    | Measure.Const(tyconRef= ucrefR) -> if tyconRefEq g (r ucrefR) ucref then OneRational else ZeroRational
    | Measure.Inv untR -> NegRational(MeasureConExponentAfterRemapping g r ucref untR)
    | Measure.Prod(measure1= unt1; measure2= unt2) -> AddRational(MeasureConExponentAfterRemapping g r ucref unt1) (MeasureConExponentAfterRemapping g r ucref unt2)
    | Measure.RationalPower(measure= untR; power= q) -> MulRational (MeasureConExponentAfterRemapping g r ucref untR) q
    | _ -> ZeroRational

/// What is the contribution of unit-of-measure variable tp to unit-of-measure expression unt? 
let rec MeasureVarExponent tp unt =
    match stripUnitEqnsFromMeasure unt with
    | Measure.Var tpR -> if typarEq tp tpR then OneRational else ZeroRational
    | Measure.Inv untR -> NegRational(MeasureVarExponent tp untR)
    | Measure.Prod(measure1= unt1; measure2= unt2) -> AddRational(MeasureVarExponent tp unt1) (MeasureVarExponent tp unt2)
    | Measure.RationalPower(measure = untR; power= q) -> MulRational (MeasureVarExponent tp untR) q
    | _ -> ZeroRational

/// List the *literal* occurrences of unit variables in a unit expression, without repeats  
let ListMeasureVarOccs unt =
    let rec gather acc unt =  
        match stripUnitEqnsFromMeasure unt with
        | Measure.Var tp -> if List.exists (typarEq tp) acc then acc else tp :: acc
        | Measure.Prod(measure1= unt1; measure2= unt2) -> gather (gather acc unt1) unt2
        | Measure.RationalPower(measure= untR) -> gather acc untR
        | Measure.Inv untR -> gather acc untR
        | _ -> acc   
    gather [] unt

/// List the *observable* occurrences of unit variables in a unit expression, without repeats, paired with their non-zero exponents
let ListMeasureVarOccsWithNonZeroExponents untexpr =
    let rec gather acc unt =  
        match stripUnitEqnsFromMeasure unt with
        | Measure.Var tp -> 
            if List.exists (fun (tpR, _) -> typarEq tp tpR) acc then acc 
            else 
                let e = MeasureVarExponent tp untexpr
                if e = ZeroRational then acc else (tp, e) :: acc
        | Measure.Prod(measure1= unt1; measure2= unt2) -> gather (gather acc unt1) unt2
        | Measure.Inv untR -> gather acc untR
        | Measure.RationalPower(measure= untR) -> gather acc untR
        | _ -> acc   
    gather [] untexpr

/// List the *observable* occurrences of unit constants in a unit expression, without repeats, paired with their non-zero exponents
let ListMeasureConOccsWithNonZeroExponents g eraseAbbrevs untexpr =
    let rec gather acc unt =  
        match (if eraseAbbrevs then stripUnitEqnsFromMeasure unt else stripUnitEqns unt) with
        | Measure.Const(tyconRef= c) -> 
            if List.exists (fun (cR, _) -> tyconRefEq g c cR) acc then acc else 
            let e = MeasureExprConExponent g eraseAbbrevs c untexpr
            if e = ZeroRational then acc else (c, e) :: acc
        | Measure.Prod(measure1= unt1; measure2= unt2) -> gather (gather acc unt1) unt2
        | Measure.Inv untR -> gather acc untR
        | Measure.RationalPower(measure= untR) -> gather acc untR
        | _ -> acc  
    gather [] untexpr

/// List the *literal* occurrences of unit constants in a unit expression, without repeats, 
/// and after applying a remapping function r to tycons
let ListMeasureConOccsAfterRemapping g r unt =
    let rec gather acc unt =  
        match stripUnitEqnsFromMeasure unt with
        | Measure.Const(tyconRef= c) -> if List.exists (tyconRefEq g (r c)) acc then acc else r c :: acc
        | Measure.Prod(measure1= unt1; measure2= unt2) -> gather (gather acc unt1) unt2
        | Measure.RationalPower(measure= untR) -> gather acc untR
        | Measure.Inv untR -> gather acc untR
        | _ -> acc
   
    gather [] unt

/// Construct a measure expression representing the n'th power of a measure
let MeasurePower u n = 
    if n = 1 then u
    elif n = 0 then Measure.One(range0)
    else Measure.RationalPower (u, intToRational n)

let MeasureProdOpt m1 m2 =
    match m1, m2 with
    | Measure.One _, _ -> m2
    | _, Measure.One _ -> m1
    | _, _ -> Measure.Prod (m1, m2, unionRanges m1.Range m2.Range)

/// Construct a measure expression representing the product of a list of measures
let ProdMeasures ms = 
    match ms with 
    | [] -> Measure.One(range0)
    | m :: ms -> List.foldBack MeasureProdOpt ms m

let isDimensionless g ty =
    match stripTyparEqns ty with
    | TType_measure unt ->
      isNil (ListMeasureVarOccsWithNonZeroExponents unt) && 
      isNil (ListMeasureConOccsWithNonZeroExponents g true unt)
    | _ -> false

let destUnitParMeasure g unt =
    let vs = ListMeasureVarOccsWithNonZeroExponents unt
    let cs = ListMeasureConOccsWithNonZeroExponents g true unt

    match vs, cs with
    | [(v, e)], [] when e = OneRational -> v
    | _, _ -> failwith "destUnitParMeasure: not a unit-of-measure parameter"

let isUnitParMeasure g unt =
    let vs = ListMeasureVarOccsWithNonZeroExponents unt
    let cs = ListMeasureConOccsWithNonZeroExponents g true unt
 
    match vs, cs with
    | [(_, e)], [] when e = OneRational -> true
    | _, _ -> false

let normalizeMeasure g ms =
    let vs = ListMeasureVarOccsWithNonZeroExponents ms
    let cs = ListMeasureConOccsWithNonZeroExponents g false ms
    match vs, cs with
    | [], [] -> Measure.One(ms.Range)
    | [(v, e)], [] when e = OneRational -> Measure.Var v
    | vs, cs ->
        List.foldBack
            (fun (v, e) ->
                fun unt ->
                    let measureVar = Measure.Var(v)
                    let measureRational = Measure.RationalPower(measureVar, e)
                    Measure.Prod(measureRational, unt, unionRanges measureRational.Range unt.Range))
            vs
            (List.foldBack
                (fun (c, e) ->
                fun unt ->
                    let measureConst = Measure.Const(c, c.Range)
                    let measureRational = Measure.RationalPower(measureConst, e)
                    let prodM = unionRanges measureConst.Range unt.Range
                    Measure.Prod(measureRational, unt, prodM)) cs (Measure.One(ms.Range)))
 
let tryNormalizeMeasureInType g ty =
    match ty with
    | TType_measure (Measure.Var v) ->
        match v.Solution with
        | Some (TType_measure ms) ->
            v.typar_solution <- Some (TType_measure (normalizeMeasure g ms))
            ty
        | _ -> ty
    | _ -> ty

//---------------------------------------------------------------------------
// Some basic type builders
//---------------------------------------------------------------------------

let mkNativePtrTy (g: TcGlobals) ty = 
    assert g.nativeptr_tcr.CanDeref // this should always be available, but check anyway
    TType_app (g.nativeptr_tcr, [ty], g.knownWithoutNull)

let mkByrefTy (g: TcGlobals) ty = 
    assert g.byref_tcr.CanDeref // this should always be available, but check anyway
    TType_app (g.byref_tcr, [ty], g.knownWithoutNull)

let mkInByrefTy (g: TcGlobals) ty = 
    if g.inref_tcr.CanDeref then // If not using sufficient FSharp.Core, then inref<T> = byref<T>, see RFC FS-1053.md
        TType_app (g.inref_tcr, [ty], g.knownWithoutNull)
    else
        mkByrefTy g ty

let mkOutByrefTy (g: TcGlobals) ty = 
    if g.outref_tcr.CanDeref then // If not using sufficient FSharp.Core, then outref<T> = byref<T>, see RFC FS-1053.md
        TType_app (g.outref_tcr, [ty], g.knownWithoutNull)
    else
        mkByrefTy g ty

let mkByrefTyWithFlag g readonly ty = 
    if readonly then 
        mkInByrefTy g ty 
    else 
        mkByrefTy g ty

let mkByref2Ty (g: TcGlobals) ty1 ty2 = 
    assert g.byref2_tcr.CanDeref // check we are using sufficient FSharp.Core, caller should check this
    TType_app (g.byref2_tcr, [ty1; ty2], g.knownWithoutNull)

let mkVoidPtrTy (g: TcGlobals) = 
    assert g.voidptr_tcr.CanDeref // check we are using sufficient FSharp.Core, caller should check this
    TType_app (g.voidptr_tcr, [], g.knownWithoutNull)

let mkByrefTyWithInference (g: TcGlobals) ty1 ty2 = 
    if g.byref2_tcr.CanDeref then // If not using sufficient FSharp.Core, then inref<T> = byref<T>, see RFC FS-1053.md
        TType_app (g.byref2_tcr, [ty1; ty2], g.knownWithoutNull) 
    else 
        TType_app (g.byref_tcr, [ty1], g.knownWithoutNull) 

let mkArrayTy (g: TcGlobals) rank nullness ty m =
    if rank < 1 || rank > 32 then
        errorR(Error(FSComp.SR.tastopsMaxArrayThirtyTwo rank, m))
        TType_app (g.il_arr_tcr_map[3], [ty], nullness)
    else
        TType_app (g.il_arr_tcr_map[rank - 1], [ty], nullness)

//--------------------------------------------------------------------------
// Tuple compilation (types)
//------------------------------------------------------------------------ 

let maxTuple = 8
let goodTupleFields = maxTuple-1

let isCompiledTupleTyconRef g tcref =
    tyconRefEq g g.ref_tuple1_tcr tcref || 
    tyconRefEq g g.ref_tuple2_tcr tcref || 
    tyconRefEq g g.ref_tuple3_tcr tcref || 
    tyconRefEq g g.ref_tuple4_tcr tcref || 
    tyconRefEq g g.ref_tuple5_tcr tcref || 
    tyconRefEq g g.ref_tuple6_tcr tcref || 
    tyconRefEq g g.ref_tuple7_tcr tcref || 
    tyconRefEq g g.ref_tuple8_tcr tcref ||
    tyconRefEq g g.struct_tuple1_tcr tcref || 
    tyconRefEq g g.struct_tuple2_tcr tcref || 
    tyconRefEq g g.struct_tuple3_tcr tcref || 
    tyconRefEq g g.struct_tuple4_tcr tcref || 
    tyconRefEq g g.struct_tuple5_tcr tcref || 
    tyconRefEq g g.struct_tuple6_tcr tcref || 
    tyconRefEq g g.struct_tuple7_tcr tcref || 
    tyconRefEq g g.struct_tuple8_tcr tcref

let mkCompiledTupleTyconRef (g: TcGlobals) isStruct n = 
    if n = 1 then (if isStruct then g.struct_tuple1_tcr else g.ref_tuple1_tcr)
    elif n = 2 then (if isStruct then g.struct_tuple2_tcr else g.ref_tuple2_tcr)
    elif n = 3 then (if isStruct then g.struct_tuple3_tcr else g.ref_tuple3_tcr)
    elif n = 4 then (if isStruct then g.struct_tuple4_tcr else g.ref_tuple4_tcr)
    elif n = 5 then (if isStruct then g.struct_tuple5_tcr else g.ref_tuple5_tcr)
    elif n = 6 then (if isStruct then g.struct_tuple6_tcr else g.ref_tuple6_tcr)
    elif n = 7 then (if isStruct then g.struct_tuple7_tcr else g.ref_tuple7_tcr)
    elif n = 8 then (if isStruct then g.struct_tuple8_tcr else g.ref_tuple8_tcr)
    else failwithf "mkCompiledTupleTyconRef, n = %d" n

/// Convert from F# tuple types to .NET tuple types
let rec mkCompiledTupleTy g isStruct tupElemTys = 
    let n = List.length tupElemTys 
    if n < maxTuple then
        TType_app (mkCompiledTupleTyconRef g isStruct n, tupElemTys, g.knownWithoutNull)
    else 
        let tysA, tysB = List.splitAfter goodTupleFields tupElemTys
        TType_app ((if isStruct then g.struct_tuple8_tcr else g.ref_tuple8_tcr), tysA@[mkCompiledTupleTy g isStruct tysB], g.knownWithoutNull)

/// Convert from F# tuple types to .NET tuple types, but only the outermost level
let mkOuterCompiledTupleTy g isStruct tupElemTys = 
    let n = List.length tupElemTys 
    if n < maxTuple then 
        TType_app (mkCompiledTupleTyconRef g isStruct n, tupElemTys, g.knownWithoutNull)
    else 
        let tysA, tysB = List.splitAfter goodTupleFields tupElemTys
        let tcref = (if isStruct then g.struct_tuple8_tcr else g.ref_tuple8_tcr)
        // In the case of an 8-tuple we add the Tuple<_> marker. For other sizes we keep the type 
        // as a regular F# tuple type.
        match tysB with 
        | [ tyB ] -> 
            let marker = TType_app (mkCompiledTupleTyconRef g isStruct 1, [tyB], g.knownWithoutNull)
            TType_app (tcref, tysA@[marker], g.knownWithoutNull)
        | _ ->
            TType_app (tcref, tysA@[TType_tuple (mkTupInfo isStruct, tysB)], g.knownWithoutNull)

//---------------------------------------------------------------------------
// Remove inference equations and abbreviations from types 
//---------------------------------------------------------------------------

let applyTyconAbbrev abbrevTy tycon tyargs = 
    if isNil tyargs then abbrevTy 
    else instType (mkTyconInst tycon tyargs) abbrevTy

let reduceTyconAbbrev (tycon: Tycon) tyargs = 
    let abbrev = tycon.TypeAbbrev
    match abbrev with 
    | None -> invalidArg "tycon" "this type definition is not an abbreviation"
    | Some abbrevTy -> 
        applyTyconAbbrev abbrevTy tycon tyargs

let reduceTyconRefAbbrev (tcref: TyconRef) tyargs = 
    reduceTyconAbbrev tcref.Deref tyargs

let reduceTyconMeasureableOrProvided (g: TcGlobals) (tycon: Tycon) tyargs =
#if NO_TYPEPROVIDERS
    ignore g  // otherwise g would be unused
#endif
    let repr = tycon.TypeReprInfo
    match repr with 
    | TMeasureableRepr ty -> 
        if isNil tyargs then ty else instType (mkTyconInst tycon tyargs) ty
#if !NO_TYPEPROVIDERS
    | TProvidedTypeRepr info when info.IsErased -> info.BaseTypeForErased (range0, g.obj_ty_withNulls)
#endif
    | _ -> invalidArg "tc" "this type definition is not a refinement" 

let reduceTyconRefMeasureableOrProvided (g: TcGlobals) (tcref: TyconRef) tyargs = 
    reduceTyconMeasureableOrProvided g tcref.Deref tyargs

let rec stripTyEqnsA g canShortcut ty = 
    let ty = stripTyparEqnsAux KnownWithoutNull canShortcut ty 
    match ty with 
    | TType_app (tcref, tinst, nullness) ->
        let tycon = tcref.Deref
        match tycon.TypeAbbrev with 
        | Some abbrevTy -> 
            let reducedTy = applyTyconAbbrev abbrevTy tycon tinst
            let reducedTy2 = addNullnessToTy nullness reducedTy
            stripTyEqnsA g canShortcut reducedTy2
        | None -> 
            // This is the point where we get to add additional conditional normalizing equations 
            // into the type system. Such power!
            // 
            // Add the equation byref<'T> = byref<'T, ByRefKinds.InOut> for when using sufficient FSharp.Core
            // See RFC FS-1053.md
            if tyconRefEq g tcref g.byref_tcr && g.byref2_tcr.CanDeref && g.byrefkind_InOut_tcr.CanDeref then 
                mkByref2Ty g tinst[0] (TType_app(g.byrefkind_InOut_tcr, [], g.knownWithoutNull))

            // Add the equation double<1> = double for units of measure.
            elif tycon.IsMeasureableReprTycon && List.forall (isDimensionless g) tinst then
                let reducedTy = reduceTyconMeasureableOrProvided g tycon tinst
                let reducedTy2 = addNullnessToTy nullness reducedTy
                stripTyEqnsA g canShortcut reducedTy2
            else 
                ty
    | ty -> ty

let stripTyEqns g ty = stripTyEqnsA g false ty

let evalTupInfoIsStruct aexpr = 
    match aexpr with 
    | TupInfo.Const b -> b

let evalAnonInfoIsStruct (anonInfo: AnonRecdTypeInfo) = 
    evalTupInfoIsStruct anonInfo.TupInfo

/// This erases outermost occurrences of inference equations, type abbreviations, non-generated provided types
/// and measurable types (float<_>).
/// It also optionally erases all "compilation representations", i.e. function and
/// tuple types, and also "nativeptr<'T> --> System.IntPtr"
let rec stripTyEqnsAndErase eraseFuncAndTuple (g: TcGlobals) ty =
    let ty = stripTyEqns g ty
    match ty with
    | TType_app (tcref, args, nullness) ->
        let tycon = tcref.Deref
        if tycon.IsErased  then
            let reducedTy = reduceTyconMeasureableOrProvided g tycon args
            let reducedTy2 = addNullnessToTy nullness reducedTy
            stripTyEqnsAndErase eraseFuncAndTuple g reducedTy2
        elif tyconRefEq g tcref g.nativeptr_tcr && eraseFuncAndTuple then 
            stripTyEqnsAndErase eraseFuncAndTuple g g.nativeint_ty
        else
            ty

    | TType_fun(domainTy, rangeTy, nullness) when eraseFuncAndTuple ->
        TType_app(g.fastFunc_tcr, [ domainTy; rangeTy ], nullness) 

    | TType_tuple(tupInfo, l) when eraseFuncAndTuple ->
        mkCompiledTupleTy g (evalTupInfoIsStruct tupInfo) l

    | ty -> ty

let stripTyEqnsAndMeasureEqns g ty =
   stripTyEqnsAndErase false g ty
       
type Erasure = EraseAll | EraseMeasures | EraseNone

let stripTyEqnsWrtErasure erasureFlag g ty = 
    match erasureFlag with 
    | EraseAll -> stripTyEqnsAndErase true g ty
    | EraseMeasures -> stripTyEqnsAndErase false g ty
    | _ -> stripTyEqns g ty
    
let rec stripExnEqns (eref: TyconRef) = 
    let exnc = eref.Deref
    match exnc.ExceptionInfo with
    | TExnAbbrevRepr eref -> stripExnEqns eref
    | _ -> exnc

let primDestForallTy g ty = ty |> stripTyEqns g |> (function TType_forall (tyvs, tau) -> (tyvs, tau) | _ -> failwith "primDestForallTy: not a forall type")

let destFunTy g ty = ty |> stripTyEqns g |> (function TType_fun (domainTy, rangeTy, _) -> (domainTy, rangeTy) | _ -> failwith "destFunTy: not a function type")

let destAnyTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple (tupInfo, l) -> tupInfo, l | _ -> failwith "destAnyTupleTy: not a tuple type")

let destRefTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple (tupInfo, l) when not (evalTupInfoIsStruct tupInfo) -> l | _ -> failwith "destRefTupleTy: not a reference tuple type")

let destStructTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple (tupInfo, l) when evalTupInfoIsStruct tupInfo -> l | _ -> failwith "destStructTupleTy: not a struct tuple type")

let destTyparTy g ty = ty |> stripTyEqns g |> (function TType_var (v, _) -> v | _ -> failwith "destTyparTy: not a typar type")

let destAnyParTy g ty = ty |> stripTyEqns g |> (function TType_var (v, _) -> v | TType_measure unt -> destUnitParMeasure g unt | _ -> failwith "destAnyParTy: not a typar or unpar type")

let destMeasureTy g ty = ty |> stripTyEqns g |> (function TType_measure m -> m | _ -> failwith "destMeasureTy: not a unit-of-measure type")

let destAnonRecdTy g ty = ty |> stripTyEqns g |> (function TType_anon (anonInfo, tys) -> anonInfo, tys | _ -> failwith "destAnonRecdTy: not an anonymous record type")

let destStructAnonRecdTy g ty = ty |> stripTyEqns g |> (function TType_anon (anonInfo, tys) when evalAnonInfoIsStruct anonInfo -> tys | _ -> failwith "destAnonRecdTy: not a struct anonymous record type")

let isFunTy g ty = ty |> stripTyEqns g |> (function TType_fun _ -> true | _ -> false)

let isForallTy g ty = ty |> stripTyEqns g |> (function TType_forall _ -> true | _ -> false)

let isAnyTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple _ -> true | _ -> false)

let isRefTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple (tupInfo, _) -> not (evalTupInfoIsStruct tupInfo) | _ -> false)

let isStructTupleTy g ty = ty |> stripTyEqns g |> (function TType_tuple (tupInfo, _) -> evalTupInfoIsStruct tupInfo | _ -> false)

let isAnonRecdTy g ty = ty |> stripTyEqns g |> (function TType_anon _ -> true | _ -> false)

let isStructAnonRecdTy g ty = ty |> stripTyEqns g |> (function TType_anon (anonInfo, _) -> evalAnonInfoIsStruct anonInfo | _ -> false)

let isUnionTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsUnionTycon | _ -> false)

let isStructUnionTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsUnionTycon && tcref.Deref.entity_flags.IsStructRecordOrUnionType | _ -> false)

let isReprHiddenTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsHiddenReprTycon | _ -> false)

let isFSharpObjModelTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsFSharpObjectModelTycon | _ -> false)

let isRecdTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsRecordTycon | _ -> false)

let isFSharpStructOrEnumTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsFSharpStructOrEnumTycon | _ -> false)

let isFSharpEnumTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsFSharpEnumTycon | _ -> false)

let isTyparTy g ty = ty |> stripTyEqns g |> (function TType_var _ -> true | _ -> false)

let isAnyParTy g ty = ty |> stripTyEqns g |> (function TType_var _ -> true | TType_measure unt -> isUnitParMeasure g unt | _ -> false)

let isMeasureTy g ty = ty |> stripTyEqns g |> (function TType_measure _ -> true | _ -> false)

let isProvenUnionCaseTy ty = match ty with TType_ucase _ -> true | _ -> false

let mkWoNullAppTy tcref tyargs = TType_app(tcref, tyargs, KnownWithoutNull)

let mkProvenUnionCaseTy ucref tyargs = TType_ucase(ucref, tyargs)

let isAppTy g ty = ty |> stripTyEqns g |> (function TType_app _ -> true | _ -> false) 

let tryAppTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, tinst, _) -> ValueSome (tcref, tinst) | _ -> ValueNone) 

let destAppTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, tinst, _) -> tcref, tinst | _ -> failwith "destAppTy")

let tcrefOfAppTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref | _ -> failwith "tcrefOfAppTy") 

let argsOfAppTy g ty = ty |> stripTyEqns g |> (function TType_app(_, tinst, _) -> tinst | _ -> [])

let tryDestTyparTy g ty = ty |> stripTyEqns g |> (function TType_var (v, _) -> ValueSome v | _ -> ValueNone)

let tryDestFunTy g ty = ty |> stripTyEqns g |> (function TType_fun (domainTy, rangeTy, _) -> ValueSome(domainTy, rangeTy) | _ -> ValueNone)

let tryTcrefOfAppTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> ValueSome tcref | _ -> ValueNone)

let tryDestAnonRecdTy g ty = ty |> stripTyEqns g |> (function TType_anon (anonInfo, tys) -> ValueSome (anonInfo, tys) | _ -> ValueNone)

let tryAnyParTy g ty = ty |> stripTyEqns g |> (function TType_var (v, _) -> ValueSome v | TType_measure unt when isUnitParMeasure g unt -> ValueSome(destUnitParMeasure g unt) | _ -> ValueNone)

let tryAnyParTyOption g ty = ty |> stripTyEqns g |> (function TType_var (v, _) -> Some v | TType_measure unt when isUnitParMeasure g unt -> Some(destUnitParMeasure g unt) | _ -> None)

[<return: Struct>]
let (|AppTy|_|) g ty = ty |> stripTyEqns g |> (function TType_app(tcref, tinst, _) -> ValueSome (tcref, tinst) | _ -> ValueNone) 

[<return: Struct>]
let (|RefTupleTy|_|) g ty = ty |> stripTyEqns g |> (function TType_tuple(tupInfo, tys) when not (evalTupInfoIsStruct tupInfo) -> ValueSome tys | _ -> ValueNone)

[<return: Struct>]
let (|FunTy|_|) g ty = ty |> stripTyEqns g |> (function TType_fun(domainTy, rangeTy, _) -> ValueSome (domainTy, rangeTy) | _ -> ValueNone)

let tryNiceEntityRefOfTy ty = 
    let ty = stripTyparEqnsAux KnownWithoutNull false ty 
    match ty with
    | TType_app (tcref, _, _) -> ValueSome tcref
    | TType_measure (Measure.Const(tyconRef= tcref)) -> ValueSome tcref
    | _ -> ValueNone

let tryNiceEntityRefOfTyOption ty = 
    let ty = stripTyparEqnsAux KnownWithoutNull false ty 
    match ty with
    | TType_app (tcref, _, _) -> Some tcref
    | TType_measure (Measure.Const(tyconRef= tcref)) -> Some tcref
    | _ -> None
    
let mkInstForAppTy g ty = 
    match tryAppTy g ty with
    | ValueSome (tcref, tinst) -> mkTyconRefInst tcref tinst
    | _ -> []

let domainOfFunTy g ty = fst (destFunTy g ty)
let rangeOfFunTy g ty = snd (destFunTy g ty)

let convertToTypeWithMetadataIfPossible g ty = 
    if isAnyTupleTy g ty then 
        let tupInfo, tupElemTys = destAnyTupleTy g ty
        mkOuterCompiledTupleTy g (evalTupInfoIsStruct tupInfo) tupElemTys
    elif isFunTy g ty then 
        let a,b = destFunTy g ty
        mkWoNullAppTy g.fastFunc_tcr [a; b]
    else ty
 
//---------------------------------------------------------------------------
// TType modifications
//---------------------------------------------------------------------------

let stripMeasuresFromTy g ty = 
    match ty with
    | TType_app(tcref, tinst, nullness) ->
        let tinstR = tinst |> List.filter (isMeasureTy g >> not)
        TType_app(tcref, tinstR, nullness)
    | _ -> ty

//---------------------------------------------------------------------------
// Equivalence of types up to alpha-equivalence 
//---------------------------------------------------------------------------


[<NoEquality; NoComparison>]
type TypeEquivEnv = 
    { EquivTypars: TyparMap<TType>
      EquivTycons: TyconRefRemap
      NullnessMustEqual : bool}

let private nullnessEqual anev (n1:Nullness) (n2:Nullness) =
    if anev.NullnessMustEqual then        
        (n1.Evaluate() = NullnessInfo.WithNull) = (n2.Evaluate() = NullnessInfo.WithNull)
    else 
        true

// allocate a singleton
let private typeEquivEnvEmpty = 
    { EquivTypars = TyparMap.Empty
      EquivTycons = emptyTyconRefRemap
      NullnessMustEqual = false}

let private typeEquivCheckNullness = {typeEquivEnvEmpty with NullnessMustEqual = true}

type TypeEquivEnv with 
    static member EmptyIgnoreNulls = typeEquivEnvEmpty
    static member EmptyWithNullChecks (g:TcGlobals) = if g.checkNullness then typeEquivCheckNullness else typeEquivEnvEmpty

    member aenv.BindTyparsToTypes tps1 tys2 =
        { aenv with EquivTypars = (tps1, tys2, aenv.EquivTypars) |||> List.foldBack2 (fun tp ty tpmap -> tpmap.Add(tp, ty)) }

    member aenv.BindEquivTypars tps1 tps2 =
        aenv.BindTyparsToTypes tps1 (List.map mkTyparTy tps2) 

    member aenv.FromTyparInst tpinst =
        let tps, tys = List.unzip tpinst
        aenv.BindTyparsToTypes tps tys 

    member aenv.FromEquivTypars tps1 tps2 = 
        aenv.BindEquivTypars tps1 tps2

    member anev.ResetEquiv = 
        if anev.NullnessMustEqual then typeEquivCheckNullness else typeEquivEnvEmpty

let rec traitsAEquivAux erasureFlag g aenv traitInfo1 traitInfo2 =
   let (TTrait(tys1, nm, mf1, argTys, retTy, _, _)) = traitInfo1
   let (TTrait(tys2, nm2, mf2, argTys2, retTy2, _, _)) = traitInfo2
   mf1.IsInstance = mf2.IsInstance &&
   nm = nm2 &&
   ListSet.equals (typeAEquivAux erasureFlag g aenv) tys1 tys2 &&
   returnTypesAEquivAux erasureFlag g aenv retTy retTy2 &&
   List.lengthsEqAndForall2 (typeAEquivAux erasureFlag g aenv) argTys argTys2

and traitKeysAEquivAux erasureFlag g aenv witnessInfo1 witnessInfo2 =
   let (TraitWitnessInfo(tys1, nm, mf1, argTys, retTy)) = witnessInfo1
   let (TraitWitnessInfo(tys2, nm2, mf2, argTys2, retTy2)) = witnessInfo2
   mf1.IsInstance = mf2.IsInstance &&
   nm = nm2 &&
   ListSet.equals (typeAEquivAux erasureFlag g aenv) tys1 tys2 &&
   returnTypesAEquivAux erasureFlag g aenv retTy retTy2 &&
   List.lengthsEqAndForall2 (typeAEquivAux erasureFlag g aenv) argTys argTys2

and returnTypesAEquivAux erasureFlag g aenv retTy retTy2 =
    match retTy, retTy2 with  
    | None, None -> true
    | Some ty1, Some ty2 -> typeAEquivAux erasureFlag g aenv ty1 ty2
    | _ -> false
    
and typarConstraintsAEquivAux erasureFlag g aenv tpc1 tpc2 =
    match tpc1, tpc2 with
    | TyparConstraint.CoercesTo(tgtTy1, _), 
      TyparConstraint.CoercesTo(tgtTy2, _) -> 
        typeAEquivAux erasureFlag g aenv tgtTy1 tgtTy2

    | TyparConstraint.MayResolveMember(trait1, _),
      TyparConstraint.MayResolveMember(trait2, _) -> 
        traitsAEquivAux erasureFlag g aenv trait1 trait2 

    | TyparConstraint.DefaultsTo(_, dfltTy1, _), 
      TyparConstraint.DefaultsTo(_, dfltTy2, _) -> 
        typeAEquivAux erasureFlag g aenv dfltTy1 dfltTy2

    | TyparConstraint.IsEnum(underlyingTy1, _), TyparConstraint.IsEnum(underlyingTy2, _) -> 
        typeAEquivAux erasureFlag g aenv underlyingTy1 underlyingTy2

    | TyparConstraint.IsDelegate(argTys1, retTy1, _), TyparConstraint.IsDelegate(argTys2, retTy2, _) -> 
        typeAEquivAux erasureFlag g aenv argTys1 argTys2 && 
        typeAEquivAux erasureFlag g aenv retTy1 retTy2 

    | TyparConstraint.SimpleChoice (tys1, _), TyparConstraint.SimpleChoice(tys2, _) -> 
        ListSet.equals (typeAEquivAux erasureFlag g aenv) tys1 tys2

    | TyparConstraint.SupportsComparison _, TyparConstraint.SupportsComparison _ 
    | TyparConstraint.SupportsEquality _, TyparConstraint.SupportsEquality _ 
    | TyparConstraint.SupportsNull _, TyparConstraint.SupportsNull _ 
    | TyparConstraint.NotSupportsNull _, TyparConstraint.NotSupportsNull _ 
    | TyparConstraint.IsNonNullableStruct _, TyparConstraint.IsNonNullableStruct _
    | TyparConstraint.IsReferenceType _, TyparConstraint.IsReferenceType _ 
    | TyparConstraint.IsUnmanaged _, TyparConstraint.IsUnmanaged _
    | TyparConstraint.AllowsRefStruct _, TyparConstraint.AllowsRefStruct _
    | TyparConstraint.RequiresDefaultConstructor _, TyparConstraint.RequiresDefaultConstructor _ -> true
    | _ -> false

and typarConstraintSetsAEquivAux erasureFlag g aenv (tp1: Typar) (tp2: Typar) = 
    tp1.StaticReq = tp2.StaticReq &&
    ListSet.equals (typarConstraintsAEquivAux erasureFlag g aenv) tp1.Constraints tp2.Constraints

and typarsAEquivAux erasureFlag g (aenv: TypeEquivEnv) tps1 tps2 = 
    List.length tps1 = List.length tps2 &&
    let aenv = aenv.BindEquivTypars tps1 tps2 
    List.forall2 (typarConstraintSetsAEquivAux erasureFlag g aenv) tps1 tps2

and tcrefAEquiv g aenv tcref1 tcref2 = 
    tyconRefEq g tcref1 tcref2 || 
      (match aenv.EquivTycons.TryFind tcref1 with Some v -> tyconRefEq g v tcref2 | None -> false)

and typeAEquivAux erasureFlag g aenv ty1 ty2 = 
    let ty1 = stripTyEqnsWrtErasure erasureFlag g ty1 
    let ty2 = stripTyEqnsWrtErasure erasureFlag g ty2
    match ty1, ty2 with
    | TType_forall(tps1, rty1), TType_forall(tps2, retTy2) -> 
        typarsAEquivAux erasureFlag g aenv tps1 tps2 && typeAEquivAux erasureFlag g (aenv.BindEquivTypars tps1 tps2) rty1 retTy2

    | TType_var (tp1, n1), TType_var (tp2, n2) when typarEq tp1 tp2 ->
        nullnessEqual aenv n1 n2 

    | TType_var (tp1, n1), _ ->
        match aenv.EquivTypars.TryFind tp1 with
        | Some tpTy1 -> 
            let tpTy1 = if (nullnessEqual aenv n1 g.knownWithoutNull) then tpTy1 else addNullnessToTy n1 tpTy1            
            typeAEquivAux erasureFlag g aenv.ResetEquiv tpTy1 ty2
        | None -> false

    | TType_app (tcref1, tinst1, n1), TType_app (tcref2, tinst2, n2) ->
        nullnessEqual aenv n1 n2  &&
        tcrefAEquiv g aenv tcref1 tcref2 &&
        typesAEquivAux erasureFlag g aenv tinst1 tinst2

    | TType_ucase (UnionCaseRef(tcref1, ucase1), tinst1), TType_ucase (UnionCaseRef(tcref2, ucase2), tinst2) -> 
        ucase1=ucase2 &&
        tcrefAEquiv g aenv tcref1 tcref2 &&
        typesAEquivAux erasureFlag g aenv tinst1 tinst2

    | TType_tuple (tupInfo1, l1), TType_tuple (tupInfo2, l2) -> 
        structnessAEquiv tupInfo1 tupInfo2 && typesAEquivAux erasureFlag g aenv l1 l2

    | TType_fun (domainTy1, rangeTy1, n1), TType_fun (domainTy2, rangeTy2, n2) ->
        nullnessEqual aenv n1 n2  &&
        typeAEquivAux erasureFlag g aenv domainTy1 domainTy2 && typeAEquivAux erasureFlag g aenv rangeTy1 rangeTy2

    | TType_anon (anonInfo1, l1), TType_anon (anonInfo2, l2) -> 
        anonInfoEquiv anonInfo1 anonInfo2 &&
        typesAEquivAux erasureFlag g aenv l1 l2

    | TType_measure m1, TType_measure m2 -> 
        match erasureFlag with 
        | EraseNone -> measureAEquiv g aenv m1 m2 
        | _ -> true 

    | _ -> false

and anonInfoEquiv (anonInfo1: AnonRecdTypeInfo) (anonInfo2: AnonRecdTypeInfo) =
    ccuEq anonInfo1.Assembly anonInfo2.Assembly && 
    structnessAEquiv anonInfo1.TupInfo anonInfo2.TupInfo && 
    anonInfo1.SortedNames = anonInfo2.SortedNames 

and structnessAEquiv un1 un2 =
    match un1, un2 with 
    | TupInfo.Const b1, TupInfo.Const b2 -> (b1 = b2)

and measureAEquiv g aenv un1 un2 =
    let vars1 = ListMeasureVarOccs un1
    let trans tp1 = match aenv.EquivTypars.TryGetValue tp1 with true, etv -> destAnyParTy g etv | false, _ -> tp1
    let remapTyconRef tcref = match aenv.EquivTycons.TryGetValue tcref with true, tval -> tval | false, _ -> tcref
    let vars1R = List.map trans vars1
    let vars2 = ListSet.subtract typarEq (ListMeasureVarOccs un2) vars1R
    let cons1 = ListMeasureConOccsAfterRemapping g remapTyconRef un1
    let cons2 = ListMeasureConOccsAfterRemapping g remapTyconRef un2 
 
    vars1 |> List.forall (fun v -> MeasureVarExponent v un1 = MeasureVarExponent (trans v) un2) &&
    vars2 |> List.forall (fun v -> MeasureVarExponent v un1 = MeasureVarExponent v un2) &&
    (cons1@cons2) |> List.forall (fun c -> MeasureConExponentAfterRemapping g remapTyconRef c un1 = MeasureConExponentAfterRemapping g remapTyconRef c un2)

and typesAEquivAux erasureFlag g aenv l1 l2 = List.lengthsEqAndForall2 (typeAEquivAux erasureFlag g aenv) l1 l2

and typeEquivAux erasureFlag g ty1 ty2 = typeAEquivAux erasureFlag g TypeEquivEnv.EmptyIgnoreNulls ty1 ty2

let typeAEquiv g aenv ty1 ty2 = typeAEquivAux EraseNone g aenv ty1 ty2

let typeEquiv g ty1 ty2 = typeEquivAux EraseNone g ty1 ty2

let traitsAEquiv g aenv t1 t2 = traitsAEquivAux EraseNone g aenv t1 t2

let traitKeysAEquiv g aenv t1 t2 = traitKeysAEquivAux EraseNone g aenv t1 t2

let typarConstraintsAEquiv g aenv c1 c2 = typarConstraintsAEquivAux EraseNone g aenv c1 c2

let typarsAEquiv g aenv d1 d2 = typarsAEquivAux EraseNone g aenv d1 d2

let returnTypesAEquiv g aenv t1 t2 = returnTypesAEquivAux EraseNone g aenv t1 t2

let measureEquiv g m1 m2 = measureAEquiv g TypeEquivEnv.EmptyIgnoreNulls m1 m2

// Get measure of type, float<_> or float32<_> or decimal<_> but not float=float<1> or float32=float32<1> or decimal=decimal<1> 
let getMeasureOfType g ty =
    match ty with 
    | AppTy g (tcref, [tyarg]) ->
        match stripTyEqns g tyarg with  
        | TType_measure ms when not (measureEquiv g ms (Measure.One(tcref.Range))) -> Some (tcref, ms)
        | _ -> None
    | _ -> None

let isErasedType g ty = 
  match stripTyEqns g ty with
#if !NO_TYPEPROVIDERS
  | TType_app (tcref, _, _) -> tcref.IsProvidedErasedTycon
#endif
  | _ -> false

// Return all components of this type expression that cannot be tested at runtime
let rec getErasedTypes g ty checkForNullness = 
    let ty = stripTyEqns g ty
    if isErasedType g ty then [ty] else 
    match ty with
    | TType_forall(_, bodyTy) -> 
        getErasedTypes g bodyTy checkForNullness

    | TType_var (tp, nullness) -> 
        match checkForNullness, nullness.Evaluate() with
        | true, NullnessInfo.WithNull -> [ty] // with-null annotations can't be tested at runtime, Nullable<> is not part of Nullness feature as of now.
        | _ -> if tp.IsErased then [ty] else []

    | TType_app (_, b, nullness) ->
        match checkForNullness, nullness.Evaluate() with
        | true, NullnessInfo.WithNull -> [ty]
        | _ -> List.foldBack (fun ty tys -> getErasedTypes g ty false @ tys) b []

    | TType_ucase(_, b) | TType_anon (_, b) | TType_tuple (_, b) ->
        List.foldBack (fun ty tys -> getErasedTypes g ty false @ tys) b []

    | TType_fun (domainTy, rangeTy, nullness) -> 
        match checkForNullness, nullness.Evaluate() with
        | true, NullnessInfo.WithNull -> [ty]
        | _ -> getErasedTypes g domainTy false @ getErasedTypes g rangeTy false
    | TType_measure _ -> 
        [ty]

//---------------------------------------------------------------------------
// Standard orderings, e.g. for order set/map keys
//---------------------------------------------------------------------------

let valOrder = { new IComparer<Val> with member _.Compare(v1, v2) = compareBy v1 v2 _.Stamp }

let tyconOrder = { new IComparer<Tycon> with member _.Compare(tycon1, tycon2) = compareBy tycon1 tycon2 _.Stamp }

let recdFieldRefOrder = 
    { new IComparer<RecdFieldRef> with 
         member _.Compare(RecdFieldRef(tcref1, nm1), RecdFieldRef(tcref2, nm2)) = 
            let c = tyconOrder.Compare (tcref1.Deref, tcref2.Deref) 
            if c <> 0 then c else 
            compare nm1 nm2 }

let unionCaseRefOrder = 
    { new IComparer<UnionCaseRef> with 
         member _.Compare(UnionCaseRef(tcref1, nm1), UnionCaseRef(tcref2, nm2)) = 
            let c = tyconOrder.Compare (tcref1.Deref, tcref2.Deref) 
            if c <> 0 then c else 
            compare nm1 nm2 }

//---------------------------------------------------------------------------
// Make some common types
//---------------------------------------------------------------------------

let mkFunTy (g: TcGlobals) domainTy rangeTy =
    TType_fun (domainTy, rangeTy, g.knownWithoutNull)

let mkForallTy d r = TType_forall (d, r)

let mkForallTyIfNeeded d r = if isNil d then r else mkForallTy d r

let (+->) d r = mkForallTyIfNeeded d r

let mkIteratedFunTy g dl r = List.foldBack (mkFunTy g) dl r

let mkLambdaTy g tps tys bodyTy = mkForallTyIfNeeded tps (mkIteratedFunTy g tys bodyTy)

let mkLambdaArgTy m tys = 
    match tys with 
    | [] -> error(InternalError("mkLambdaArgTy", m))
    | [h] -> h 
    | _ -> mkRawRefTupleTy tys

let typeOfLambdaArg m vs = mkLambdaArgTy m (typesOfVals vs)

let mkMultiLambdaTy g m vs bodyTy = mkFunTy g (typeOfLambdaArg m vs) bodyTy 

/// When compiling FSharp.Core.dll we have to deal with the non-local references into
/// the library arising from env.fs. Part of this means that we have to be able to resolve these
/// references. This function artificially forces the existence of a module or namespace at a 
/// particular point in order to do this.
let ensureCcuHasModuleOrNamespaceAtPath (ccu: CcuThunk) path (CompPath(_, sa, cpath)) xml =
    let scoref = ccu.ILScopeRef 
    let rec loop prior_cpath (path: Ident list) cpath (modul: ModuleOrNamespace) =
        let mtype = modul.ModuleOrNamespaceType 
        match path, cpath with 
        | hpath :: tpath, (_, mkind) :: tcpath -> 
            let modName = hpath.idText 
            if not (Map.containsKey modName mtype.AllEntitiesByCompiledAndLogicalMangledNames) then 
                let mty = Construct.NewEmptyModuleOrNamespaceType mkind
                let cpath = CompPath(scoref, sa, prior_cpath)
                let smodul = Construct.NewModuleOrNamespace (Some cpath) taccessPublic hpath xml [] (MaybeLazy.Strict mty)
                mtype.AddModuleOrNamespaceByMutation smodul
            let modul = Map.find modName mtype.AllEntitiesByCompiledAndLogicalMangledNames 
            loop (prior_cpath @ [(modName, Namespace true)]) tpath tcpath modul 

        | _ -> () 

    loop [] path cpath ccu.Contents


//---------------------------------------------------------------------------
// Primitive destructors
//---------------------------------------------------------------------------

/// Look through the Expr.Link nodes arising from type inference
let rec stripExpr e = 
    match e with 
    | Expr.Link eref -> stripExpr eref.Value
    | _ -> e    

let rec stripDebugPoints expr = 
    match stripExpr expr with
    | Expr.DebugPoint (_, innerExpr) -> stripDebugPoints innerExpr
    | expr -> expr

// Strip debug points and remember how to recreate them
let (|DebugPoints|) expr =
    let rec loop expr debug =
        match stripExpr expr with
        | Expr.DebugPoint (dp, innerExpr) -> loop innerExpr (debug << fun e -> Expr.DebugPoint (dp, e))
        | expr -> expr, debug

    loop expr id

let mkCase (a, b) = TCase(a, b)

let isRefTupleExpr e = match e with Expr.Op (TOp.Tuple tupInfo, _, _, _) -> not (evalTupInfoIsStruct tupInfo) | _ -> false

let tryDestRefTupleExpr e = match e with Expr.Op (TOp.Tuple tupInfo, _, es, _) when not (evalTupInfoIsStruct tupInfo) -> es | _ -> [e]

//---------------------------------------------------------------------------
// Build nodes in decision graphs
//---------------------------------------------------------------------------


let primMkMatch(spBind, mExpr, tree, targets, mMatch, ty) = Expr.Match (spBind, mExpr, tree, targets, mMatch, ty)

type MatchBuilder(spBind, inpRange: range) = 

    let targets = ResizeArray<_>(10) 
    member x.AddTarget tg = 
        let n = targets.Count 
        targets.Add tg
        n

    member x.AddResultTarget(e) = TDSuccess([], x.AddTarget(TTarget([], e, None)))

    member _.CloseTargets() = targets |> ResizeArray.toList

    member _.Close(dtree, m, ty) = primMkMatch (spBind, inpRange, dtree, targets.ToArray(), m, ty)

let mkBoolSwitch m g t e =
    TDSwitch(g, [TCase(DecisionTreeTest.Const(Const.Bool true), t)], Some e, m)

let primMkCond spBind m ty e1 e2 e3 = 
    let mbuilder = MatchBuilder(spBind, m)
    let dtree = mkBoolSwitch m e1 (mbuilder.AddResultTarget(e2)) (mbuilder.AddResultTarget(e3)) 
    mbuilder.Close(dtree, m, ty)

let mkCond spBind m ty e1 e2 e3 =
    primMkCond spBind m ty e1 e2 e3

//---------------------------------------------------------------------------
// Primitive constructors
//---------------------------------------------------------------------------

let exprForValRef m vref = Expr.Val (vref, NormalValUse, m)
let exprForVal m v = exprForValRef m (mkLocalValRef v)
let mkLocalAux m s ty mut compgen =
    let thisv = Construct.NewVal(s, m, None, ty, mut, compgen, None, taccessPublic, ValNotInRecScope, None, NormalVal, [], ValInline.Optional, XmlDoc.Empty, false, false, false, false, false, false, None, ParentNone) 
    thisv, exprForVal m thisv

let mkLocal m s ty = mkLocalAux m s ty Immutable false
let mkCompGenLocal m s ty = mkLocalAux m s ty Immutable true
let mkMutableCompGenLocal m s ty = mkLocalAux m s ty Mutable true

// Type gives return type. For type-lambdas this is the formal return type. 
let mkMultiLambda m vs (body, bodyTy) = Expr.Lambda (newUnique(), None, None, vs, body, m, bodyTy)

let rebuildLambda m ctorThisValOpt baseValOpt vs (body, bodyTy) = Expr.Lambda (newUnique(), ctorThisValOpt, baseValOpt, vs, body, m, bodyTy)

let mkLambda m v (body, bodyTy) = mkMultiLambda m [v] (body, bodyTy)

let mkTypeLambda m vs (body, bodyTy) = match vs with [] -> body | _ -> Expr.TyLambda (newUnique(), vs, body, m, bodyTy)

let mkTypeChoose m vs body = match vs with [] -> body | _ -> Expr.TyChoose (vs, body, m)

let mkObjExpr (ty, basev, basecall, overrides, iimpls, m) = 
    Expr.Obj (newUnique(), ty, basev, basecall, overrides, iimpls, m) 

let mkLambdas g m tps (vs: Val list) (body, bodyTy) = 
    mkTypeLambda m tps (List.foldBack (fun v (e, ty) -> mkLambda m v (e, ty), mkFunTy g v.Type ty) vs (body, bodyTy))

let mkMultiLambdasCore g m vsl (body, bodyTy) = 
    List.foldBack (fun v (e, ty) -> mkMultiLambda m v (e, ty), mkFunTy g (typeOfLambdaArg m v) ty) vsl (body, bodyTy)

let mkMultiLambdas g m tps vsl (body, bodyTy) = 
    mkTypeLambda m tps (mkMultiLambdasCore g m vsl (body, bodyTy) )

let mkMemberLambdas g m tps ctorThisValOpt baseValOpt vsl (body, bodyTy) = 
    let expr = 
        match ctorThisValOpt, baseValOpt with
        | None, None -> mkMultiLambdasCore g m vsl (body, bodyTy)
        | _ -> 
            match vsl with 
            | [] -> error(InternalError("mk_basev_multi_lambdas_core: can't attach a basev to a non-lambda expression", m))
            | h :: t -> 
                let body, bodyTy = mkMultiLambdasCore g m t (body, bodyTy)
                (rebuildLambda m ctorThisValOpt baseValOpt h (body, bodyTy), (mkFunTy g (typeOfLambdaArg m h) bodyTy))
    mkTypeLambda m tps expr

let mkMultiLambdaBind g v letSeqPtOpt m tps vsl (body, bodyTy) = 
    TBind(v, mkMultiLambdas g m tps vsl (body, bodyTy), letSeqPtOpt)

let mkBind seqPtOpt v e = TBind(v, e, seqPtOpt)

let mkLetBind m bind body = Expr.Let (bind, body, m, Construct.NewFreeVarsCache())

let mkLetsBind m binds body = List.foldBack (mkLetBind m) binds body 

let mkLetsFromBindings m binds body = List.foldBack (mkLetBind m) binds body 

let mkLet seqPtOpt m v x body = mkLetBind m (mkBind seqPtOpt v x) body

/// Make sticky bindings that are compiler generated (though the variables may not be - e.g. they may be lambda arguments in a beta reduction)
let mkCompGenBind v e = TBind(v, e, DebugPointAtBinding.NoneAtSticky)

let mkCompGenBinds (vs: Val list) (es: Expr list) = List.map2 mkCompGenBind vs es

let mkCompGenLet m v x body = mkLetBind m (mkCompGenBind v x) body

let mkInvisibleBind v e = TBind(v, e, DebugPointAtBinding.NoneAtInvisible)

let mkInvisibleBinds (vs: Val list) (es: Expr list) = List.map2 mkInvisibleBind vs es

let mkInvisibleLet m v x body = mkLetBind m (mkInvisibleBind v x) body

let mkInvisibleLets m vs xs body = mkLetsBind m (mkInvisibleBinds vs xs) body

let mkInvisibleLetsFromBindings m vs xs body = mkLetsFromBindings m (mkInvisibleBinds vs xs) body

let mkLetRecBinds m binds body =
    if isNil binds then
        body 
    else
        Expr.LetRec (binds, body, m, Construct.NewFreeVarsCache())

//-------------------------------------------------------------------------
// Type schemes...
//-------------------------------------------------------------------------

// Type parameters may be have been equated to other tps in equi-recursive type inference 
// and unit type inference. Normalize them here 
let NormalizeDeclaredTyparsForEquiRecursiveInference g tps = 
    match tps with 
    | [] -> []
    | tps -> 
        tps |> List.map (fun tp ->
          let ty = mkTyparTy tp
          match tryAnyParTy g ty with
          | ValueSome anyParTy -> anyParTy 
          | ValueNone -> tp)
 
type GeneralizedType = GeneralizedType of Typars * TType    
  
let mkGenericBindRhs g m generalizedTyparsForRecursiveBlock typeScheme bodyExpr = 
    let (GeneralizedType(generalizedTypars, tauTy)) = typeScheme

    // Normalize the generalized typars
    let generalizedTypars = NormalizeDeclaredTyparsForEquiRecursiveInference g generalizedTypars

    // Some recursive bindings result in free type variables, e.g. 
    //    let rec f (x:'a) = ()  
    //    and g() = f y |> ignore 
    // What is the type of y? Type inference equates it to 'a. 
    // But "g" is not polymorphic in 'a. Hence we get a free choice of "'a" 
    // in the scope of "g". Thus at each individual recursive binding we record all 
    // type variables for which we have a free choice, which is precisely the difference 
    // between the union of all sets of generalized type variables and the set generalized 
    // at each particular binding. 
    //
    // We record an expression node that indicates that a free choice can be made 
    // for these. This expression node effectively binds the type variables. 
    let freeChoiceTypars = ListSet.subtract typarEq generalizedTyparsForRecursiveBlock generalizedTypars
    mkTypeLambda m generalizedTypars (mkTypeChoose m freeChoiceTypars bodyExpr, tauTy)

let isBeingGeneralized tp typeScheme = 
    let (GeneralizedType(generalizedTypars, _)) = typeScheme
    ListSet.contains typarRefEq tp generalizedTypars

//-------------------------------------------------------------------------
// Build conditional expressions...
//------------------------------------------------------------------------- 

let mkBool (g: TcGlobals) m b =
    Expr.Const (Const.Bool b, m, g.bool_ty)

let mkTrue g m =
    mkBool g m true

let mkFalse g m =
    mkBool g m false

let mkLazyOr (g: TcGlobals) m e1 e2 =
    mkCond DebugPointAtBinding.NoneAtSticky m g.bool_ty e1 (mkTrue g m) e2

let mkLazyAnd (g: TcGlobals) m e1 e2 =
    mkCond DebugPointAtBinding.NoneAtSticky m g.bool_ty e1 e2 (mkFalse g m)

let mkCoerceExpr(e, toTy, m, fromTy) =
    Expr.Op (TOp.Coerce, [toTy; fromTy], [e], m)

let mkAsmExpr (code, tinst, args, rettys, m) =
    Expr.Op (TOp.ILAsm (code, rettys), tinst, args, m)

let mkUnionCaseExpr(uc, tinst, args, m) =
    Expr.Op (TOp.UnionCase uc, tinst, args, m)

let mkExnExpr(uc, args, m) =
    Expr.Op (TOp.ExnConstr uc, [], args, m)

let mkTupleFieldGetViaExprAddr(tupInfo, e, tinst, i, m) =
    Expr.Op (TOp.TupleFieldGet (tupInfo, i), tinst, [e], m)

let mkAnonRecdFieldGetViaExprAddr(anonInfo, e, tinst, i, m) =
    Expr.Op (TOp.AnonRecdGet (anonInfo, i), tinst, [e], m)

let mkRecdFieldGetViaExprAddr (e, fref, tinst, m) =
    Expr.Op (TOp.ValFieldGet fref, tinst, [e], m)

let mkRecdFieldGetAddrViaExprAddr(readonly, e, fref, tinst, m) =
    Expr.Op (TOp.ValFieldGetAddr (fref, readonly), tinst, [e], m)

let mkStaticRecdFieldGetAddr(readonly, fref, tinst, m) =
    Expr.Op (TOp.ValFieldGetAddr (fref, readonly), tinst, [], m)

let mkStaticRecdFieldGet (fref, tinst, m) =
    Expr.Op (TOp.ValFieldGet fref, tinst, [], m)

let mkStaticRecdFieldSet(fref, tinst, e, m) =
    Expr.Op (TOp.ValFieldSet fref, tinst, [e], m)

let mkArrayElemAddress g (readonly, ilInstrReadOnlyAnnotation, isNativePtr, shape, elemTy, exprs, m) = 
    Expr.Op (TOp.ILAsm ([I_ldelema(ilInstrReadOnlyAnnotation, isNativePtr, shape, mkILTyvarTy 0us)], [mkByrefTyWithFlag g readonly elemTy]), [elemTy], exprs, m)

let mkRecdFieldSetViaExprAddr (e1, fref, tinst, e2, m) =
    Expr.Op (TOp.ValFieldSet fref, tinst, [e1;e2], m)

let mkUnionCaseTagGetViaExprAddr (e1, cref, tinst, m) =
    Expr.Op (TOp.UnionCaseTagGet cref, tinst, [e1], m)

/// Make a 'TOp.UnionCaseProof' expression, which proves a union value is over a particular case (used only for ref-unions, not struct-unions)
let mkUnionCaseProof (e1, cref: UnionCaseRef, tinst, m) =
    if cref.Tycon.IsStructOrEnumTycon then e1 else Expr.Op (TOp.UnionCaseProof cref, tinst, [e1], m)

/// Build a 'TOp.UnionCaseFieldGet' expression for something we've already determined to be a particular union case. For ref-unions, 
/// the input expression has 'TType_ucase', which is an F# compiler internal "type" corresponding to the union case. For struct-unions, 
/// the input should be the address of the expression.
let mkUnionCaseFieldGetProvenViaExprAddr (e1, cref, tinst, j, m) =
    Expr.Op (TOp.UnionCaseFieldGet (cref, j), tinst, [e1], m)

/// Build a 'TOp.UnionCaseFieldGetAddr' expression for a field of a union when we've already determined the value to be a particular union case. For ref-unions, 
/// the input expression has 'TType_ucase', which is an F# compiler internal "type" corresponding to the union case. For struct-unions, 
/// the input should be the address of the expression.
let mkUnionCaseFieldGetAddrProvenViaExprAddr (readonly, e1, cref, tinst, j, m) =
    Expr.Op (TOp.UnionCaseFieldGetAddr (cref, j, readonly), tinst, [e1], m)

/// Build a 'get' expression for something we've already determined to be a particular union case, but where 
/// the static type of the input is not yet proven to be that particular union case. This requires a type
/// cast to 'prove' the condition.
let mkUnionCaseFieldGetUnprovenViaExprAddr (e1, cref, tinst, j, m) =
    mkUnionCaseFieldGetProvenViaExprAddr (mkUnionCaseProof(e1, cref, tinst, m), cref, tinst, j, m)

let mkUnionCaseFieldSet (e1, cref, tinst, j, e2, m) =
    Expr.Op (TOp.UnionCaseFieldSet (cref, j), tinst, [e1;e2], m)

let mkExnCaseFieldGet (e1, ecref, j, m) =
    Expr.Op (TOp.ExnFieldGet (ecref, j), [], [e1], m)

let mkExnCaseFieldSet (e1, ecref, j, e2, m) =
    Expr.Op (TOp.ExnFieldSet (ecref, j), [], [e1;e2], m)

let mkDummyLambda (g: TcGlobals) (bodyExpr: Expr, bodyExprTy) = 
    let m = bodyExpr.Range
    mkLambda m (fst (mkCompGenLocal m "unitVar" g.unit_ty)) (bodyExpr, bodyExprTy)
                           
let mkWhile (g: TcGlobals) (spWhile, marker, guardExpr, bodyExpr, m) = 
    Expr.Op (TOp.While (spWhile, marker), [], [mkDummyLambda g (guardExpr, g.bool_ty);mkDummyLambda g (bodyExpr, g.unit_ty)], m)

let mkIntegerForLoop (g: TcGlobals) (spFor, spIn, v, startExpr, dir, finishExpr, bodyExpr: Expr, m) = 
    Expr.Op (TOp.IntegerForLoop (spFor, spIn, dir), [], [mkDummyLambda g (startExpr, g.int_ty) ;mkDummyLambda g (finishExpr, g.int_ty);mkLambda bodyExpr.Range v (bodyExpr, g.unit_ty)], m)

let mkTryWith g (bodyExpr, filterVal, filterExpr: Expr, handlerVal, handlerExpr: Expr, m, ty, spTry, spWith) = 
    Expr.Op (TOp.TryWith (spTry, spWith), [ty], [mkDummyLambda g (bodyExpr, ty);mkLambda filterExpr.Range filterVal (filterExpr, ty);mkLambda handlerExpr.Range handlerVal (handlerExpr, ty)], m)

let mkTryFinally (g: TcGlobals) (bodyExpr, finallyExpr, m, ty, spTry, spFinally) = 
    Expr.Op (TOp.TryFinally (spTry, spFinally), [ty], [mkDummyLambda g (bodyExpr, ty);mkDummyLambda g (finallyExpr, g.unit_ty)], m)

let mkDefault (m, ty) =
    Expr.Const (Const.Zero, m, ty) 

let mkValSet m vref e =
    Expr.Op (TOp.LValueOp (LSet, vref), [], [e], m)             

let mkAddrSet m vref e =
    Expr.Op (TOp.LValueOp (LByrefSet, vref), [], [e], m)       

let mkAddrGet m vref =
    Expr.Op (TOp.LValueOp (LByrefGet, vref), [], [], m)          

let mkValAddr m readonly vref =
    Expr.Op (TOp.LValueOp (LAddrOf readonly, vref), [], [], m)           

//--------------------------------------------------------------------------
// Maps tracking extra information for values
//--------------------------------------------------------------------------

[<NoEquality; NoComparison>]
type ValHash<'T> = 
    | ValHash of Dictionary<Stamp, 'T>

    member ht.Values = 
        let (ValHash t) = ht
        t.Values :> seq<'T>

    member ht.TryFind (v: Val) = 
        let (ValHash t) = ht
        match t.TryGetValue v.Stamp with
        | true, v -> Some v
        | _ -> None

    member ht.Add (v: Val, x) = 
        let (ValHash t) = ht
        t[v.Stamp] <- x

    static member Create() = ValHash (new Dictionary<_, 'T>(11))

[<Struct; NoEquality; NoComparison>]
type ValMultiMap<'T>(contents: StampMap<'T list>) =

    member _.ContainsKey (v: Val) =
        contents.ContainsKey v.Stamp

    member _.Find (v: Val) =
        match contents |> Map.tryFind v.Stamp with
        | Some vals -> vals
        | _ -> []

    member m.Add (v: Val, x) = ValMultiMap<'T>(contents.Add (v.Stamp, x :: m.Find v))

    member _.Remove (v: Val) = ValMultiMap<'T>(contents.Remove v.Stamp)

    member _.Contents = contents

    static member Empty = ValMultiMap<'T>(Map.empty)

[<Struct; NoEquality; NoComparison>]
type TyconRefMultiMap<'T>(contents: TyconRefMap<'T list>) =

    member _.Find v = 
        match contents.TryFind v with
        | Some vals -> vals
        | _ -> []

    member m.Add (v, x) = TyconRefMultiMap<'T>(contents.Add v (x :: m.Find v))

    static member Empty = TyconRefMultiMap<'T>(TyconRefMap<_>.Empty)

    static member OfList vs = (vs, TyconRefMultiMap<'T>.Empty) ||> List.foldBack (fun (x, y) acc -> acc.Add (x, y)) 

//--------------------------------------------------------------------------
// From Ref_private to Ref_nonlocal when exporting data.
//--------------------------------------------------------------------------

/// Try to create a EntityRef suitable for accessing the given Entity from another assembly 
let tryRescopeEntity viewedCcu (entity: Entity) : EntityRef voption =
    match entity.PublicPath with 
    | Some pubpath -> ValueSome (ERefNonLocal (rescopePubPath viewedCcu pubpath))
    | None -> ValueNone

/// Try to create a ValRef suitable for accessing the given Val from another assembly 
let tryRescopeVal viewedCcu (entityRemap: Remap) (vspec: Val) : ValRef voption =
    match vspec.PublicPath with 
    | Some (ValPubPath(p, fullLinkageKey)) -> 
        // The type information in the val linkage doesn't need to keep any information to trait solutions.
        let entityRemap = { entityRemap with removeTraitSolutions = true }
        let fullLinkageKey = remapValLinkage entityRemap fullLinkageKey
        let vref = 
            // This compensates for the somewhat poor design decision in the F# compiler and metadata where
            // members are stored as values under the enclosing namespace/module rather than under the type.
            // This stems from the days when types and namespace/modules were separated constructs in the 
            // compiler implementation.
            if vspec.IsIntrinsicMember then  
                mkNonLocalValRef (rescopePubPathToParent viewedCcu p) fullLinkageKey
            else 
                mkNonLocalValRef (rescopePubPath viewedCcu p) fullLinkageKey
        ValueSome vref
    | _ -> ValueNone
    
//---------------------------------------------------------------------------
// Type information about records, constructors etc.
//---------------------------------------------------------------------------
 
let actualTyOfRecdField inst (fspec: RecdField) = instType inst fspec.FormalType

let actualTysOfRecdFields inst rfields = List.map (actualTyOfRecdField inst) rfields

let actualTysOfInstanceRecdFields inst (tcref: TyconRef) = tcref.AllInstanceFieldsAsList |> actualTysOfRecdFields inst 

let actualTysOfUnionCaseFields inst (x: UnionCaseRef) = actualTysOfRecdFields inst x.AllFieldsAsList

let actualResultTyOfUnionCase tinst (x: UnionCaseRef) = 
    instType (mkTyconRefInst x.TyconRef tinst) x.ReturnType

let recdFieldsOfExnDefRef x =
    (stripExnEqns x).TrueInstanceFieldsAsList

let recdFieldOfExnDefRefByIdx x n =
    (stripExnEqns x).GetFieldByIndex n

let recdFieldTysOfExnDefRef x =
    actualTysOfRecdFields [] (recdFieldsOfExnDefRef x)

let recdFieldTyOfExnDefRefByIdx x j =
    actualTyOfRecdField [] (recdFieldOfExnDefRefByIdx x j)

let actualTyOfRecdFieldForTycon tycon tinst (fspec: RecdField) = 
    instType (mkTyconInst tycon tinst) fspec.FormalType

let actualTyOfRecdFieldRef (fref: RecdFieldRef) tinst = 
    actualTyOfRecdFieldForTycon fref.Tycon tinst fref.RecdField

let actualTyOfUnionFieldRef (fref: UnionCaseRef) n tinst = 
    actualTyOfRecdFieldForTycon fref.Tycon tinst (fref.FieldByIndex n)

    
//---------------------------------------------------------------------------
// Apply type functions to types
//---------------------------------------------------------------------------

let destForallTy g ty = 
    let tps, tau = primDestForallTy g ty 
    // tps may be have been equated to other tps in equi-recursive type inference 
    // and unit type inference. Normalize them here 
    let tps = NormalizeDeclaredTyparsForEquiRecursiveInference g tps
    tps, tau

let tryDestForallTy g ty = 
    if isForallTy g ty then destForallTy g ty else [], ty

let rec stripFunTy g ty = 
    if isFunTy g ty then 
        let domainTy, rangeTy = destFunTy g ty 
        let more, retTy = stripFunTy g rangeTy 
        domainTy :: more, retTy
    else [], ty

let applyForallTy g ty tyargs = 
    let tps, tau = destForallTy g ty
    instType (mkTyparInst tps tyargs) tau

let reduceIteratedFunTy g ty args = 
    List.fold (fun ty _ -> 
        if not (isFunTy g ty) then failwith "reduceIteratedFunTy"
        snd (destFunTy g ty)) ty args

let applyTyArgs g ty tyargs = 
    if isForallTy g ty then applyForallTy g ty tyargs else ty

let applyTys g funcTy (tyargs, argTys) = 
    let afterTyappTy = applyTyArgs g funcTy tyargs
    reduceIteratedFunTy g afterTyappTy argTys

let formalApplyTys g funcTy (tyargs, args) = 
    reduceIteratedFunTy g
      (if isNil tyargs then funcTy else snd (destForallTy g funcTy))
      args

let rec stripFunTyN g n ty = 
    assert (n >= 0)
    if n > 0 && isFunTy g ty then 
        let d, r = destFunTy g ty
        let more, retTy = stripFunTyN g (n-1) r
        d :: more, retTy
    else [], ty
        
let tryDestAnyTupleTy g ty = 
    if isAnyTupleTy g ty then destAnyTupleTy g ty else tupInfoRef, [ty]

let tryDestRefTupleTy g ty = 
    if isRefTupleTy g ty then destRefTupleTy g ty else [ty]

type UncurriedArgInfos = (TType * ArgReprInfo) list 

type CurriedArgInfos = (TType * ArgReprInfo) list list

type TraitWitnessInfos = TraitWitnessInfo list

// A 'tau' type is one with its type parameters stripped off 
let GetTopTauTypeInFSharpForm g (curriedArgInfos: ArgReprInfo list list) tau m =
    let nArgInfos = curriedArgInfos.Length
    let argTys, retTy = stripFunTyN g nArgInfos tau

    if nArgInfos <> argTys.Length then 
        error(Error(FSComp.SR.tastInvalidMemberSignature(), m))

    let argTysl = 
        (curriedArgInfos, argTys) ||> List.map2 (fun argInfos argTy -> 
            match argInfos with 
            | [] -> [ (g.unit_ty, ValReprInfo.unnamedTopArg1) ]
            | [argInfo] -> [ (argTy, argInfo) ]
            | _ -> List.zip (destRefTupleTy g argTy) argInfos) 

    argTysl, retTy

let destTopForallTy g (ValReprInfo (ntps, _, _)) ty =
    let tps, tau = (if isNil ntps then [], ty else tryDestForallTy g ty)
    // tps may be have been equated to other tps in equi-recursive type inference. Normalize them here 
    let tps = NormalizeDeclaredTyparsForEquiRecursiveInference g tps
    tps, tau

let GetValReprTypeInFSharpForm g (ValReprInfo(_, argInfos, retInfo) as valReprInfo) ty m =
    let tps, tau = destTopForallTy g valReprInfo ty
    let curriedArgTys, returnTy = GetTopTauTypeInFSharpForm g argInfos tau m
    tps, curriedArgTys, returnTy, retInfo

let IsCompiledAsStaticProperty g (v: Val) =
    match v.ValReprInfo with
    | Some valReprInfoValue ->
         match GetValReprTypeInFSharpForm g valReprInfoValue v.Type v.Range with 
         | [], [], _, _ when not v.IsMember -> true
         | _ -> false
    | _ -> false

let IsCompiledAsStaticPropertyWithField g (v: Val) = 
    not v.IsCompiledAsStaticPropertyWithoutField &&
    IsCompiledAsStaticProperty g v

//-------------------------------------------------------------------------
// Multi-dimensional array types...
//-------------------------------------------------------------------------

let isArrayTyconRef (g: TcGlobals) tcref =
    g.il_arr_tcr_map
    |> Array.exists (tyconRefEq g tcref)

let rankOfArrayTyconRef (g: TcGlobals) tcref =
    match g.il_arr_tcr_map |> Array.tryFindIndex (tyconRefEq g tcref) with
    | Some idx ->
        idx + 1
    | None ->
        failwith "rankOfArrayTyconRef: unsupported array rank"

//-------------------------------------------------------------------------
// Misc functions on F# types
//------------------------------------------------------------------------- 

let destArrayTy (g: TcGlobals) ty =
    match tryAppTy g ty with
    | ValueSome (tcref, [ty]) when isArrayTyconRef g tcref -> ty
    | _ -> failwith "destArrayTy"

let destListTy (g: TcGlobals) ty =
    match tryAppTy g ty with
    | ValueSome (tcref, [ty]) when tyconRefEq g tcref g.list_tcr_canon -> ty
    | _ -> failwith "destListTy"

let tyconRefEqOpt g tcrefOpt tcref = 
    match tcrefOpt with
    | None -> false
    | Some tcref2 -> tyconRefEq g tcref2 tcref

let isStringTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g tcref g.system_String_tcref | _ -> false)

let isListTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g tcref g.list_tcr_canon | _ -> false)

let isArrayTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> isArrayTyconRef g tcref | _ -> false) 

let isArray1DTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g tcref g.il_arr_tcr_map[0] | _ -> false) 

let isUnitTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g g.unit_tcr_canon tcref | _ -> false) 

let isObjTyAnyNullness g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g g.system_Object_tcref tcref | _ -> false) 

let isObjNullTy g ty = 
    ty 
    |> stripTyEqns g 
    |> (function TType_app(tcref, _, n) when (not g.checkNullness) || (n.TryEvaluate() <> ValueSome(NullnessInfo.WithoutNull)) 
                -> tyconRefEq g g.system_Object_tcref tcref | _ -> false)

let isObjTyWithoutNull (g:TcGlobals) ty = 
    g.checkNullness &&
    ty 
    |> stripTyEqns g 
    |> (function TType_app(tcref, _, n) when (n.TryEvaluate() = ValueSome(NullnessInfo.WithoutNull)) 
                -> tyconRefEq g g.system_Object_tcref tcref | _ -> false) 

let isValueTypeTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g g.system_Value_tcref tcref | _ -> false) 

let isVoidTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g g.system_Void_tcref tcref | _ -> false) 

let isILAppTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.IsILTycon | _ -> false) 

let isNativePtrTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g g.nativeptr_tcr tcref | _ -> false) 

let isByrefTy g ty = 
    ty |> stripTyEqns g |> (function 
        | TType_app(tcref, _, _) when g.byref2_tcr.CanDeref -> tyconRefEq g g.byref2_tcr tcref
        | TType_app(tcref, _, _) -> tyconRefEq g g.byref_tcr tcref
        | _ -> false) 

let isInByrefTag g ty = ty |> stripTyEqns g |> (function TType_app(tcref, [], _) -> tyconRefEq g g.byrefkind_In_tcr tcref | _ -> false) 
let isInByrefTy g ty = 
    ty |> stripTyEqns g |> (function 
        | TType_app(tcref, [_; tagTy], _) when g.byref2_tcr.CanDeref -> tyconRefEq g g.byref2_tcr tcref && isInByrefTag g tagTy         
        | _ -> false) 

let isOutByrefTag g ty = ty |> stripTyEqns g |> (function TType_app(tcref, [], _) -> tyconRefEq g g.byrefkind_Out_tcr tcref | _ -> false) 

let isOutByrefTy g ty = 
    ty |> stripTyEqns g |> (function 
        | TType_app(tcref, [_; tagTy], _) when g.byref2_tcr.CanDeref -> tyconRefEq g g.byref2_tcr tcref && isOutByrefTag g tagTy         
        | _ -> false) 

#if !NO_TYPEPROVIDERS
let extensionInfoOfTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tcref.TypeReprInfo | _ -> TNoRepr) 
#endif

type TypeDefMetadata = 
     | ILTypeMetadata of TILObjectReprData
     | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata 
#if !NO_TYPEPROVIDERS
     | ProvidedTypeMetadata of TProvidedTypeInfo
#endif

let metadataOfTycon (tycon: Tycon) = 
#if !NO_TYPEPROVIDERS
    match tycon.TypeReprInfo with 
    | TProvidedTypeRepr info -> ProvidedTypeMetadata info
    | _ -> 
#endif
    if tycon.IsILTycon then 
       ILTypeMetadata tycon.ILTyconInfo
    else 
       FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata 


let metadataOfTy g ty = 
#if !NO_TYPEPROVIDERS
    match extensionInfoOfTy g ty with 
    | TProvidedTypeRepr info -> ProvidedTypeMetadata info
    | _ -> 
#endif
    if isILAppTy g ty then 
        let tcref = tcrefOfAppTy g ty
        ILTypeMetadata tcref.ILTyconInfo
    else 
        FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata 


let isILReferenceTy g ty = 
    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> not info.IsStructOrEnum
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> not td.IsStructOrEnum
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> isArrayTy g ty

let isILInterfaceTycon (tycon: Tycon) = 
    match metadataOfTycon tycon with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> info.IsInterface
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> td.IsInterface
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> false

let rankOfArrayTy g ty = rankOfArrayTyconRef g (tcrefOfAppTy g ty)

let isFSharpObjModelRefTy g ty = 
    isFSharpObjModelTy g ty && 
    let tcref = tcrefOfAppTy g ty
    match tcref.FSharpTyconRepresentationData.fsobjmodel_kind with 
    | TFSharpClass | TFSharpInterface | TFSharpDelegate _ -> true
    | TFSharpUnion | TFSharpRecord | TFSharpStruct | TFSharpEnum -> false

let isFSharpClassTy g ty =
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.Deref.IsFSharpClassTycon
    | _ -> false

let isFSharpStructTy g ty =
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.Deref.IsFSharpStructOrEnumTycon
    | _ -> false

let isFSharpInterfaceTy g ty = 
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.Deref.IsFSharpInterfaceTycon
    | _ -> false

let isDelegateTy g ty = 
    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> info.IsDelegate ()
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> td.IsDelegate
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata ->
        match tryTcrefOfAppTy g ty with
        | ValueSome tcref -> tcref.Deref.IsFSharpDelegateTycon
        | _ -> false

let isInterfaceTy g ty = 
    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> info.IsInterface
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> td.IsInterface
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> isFSharpInterfaceTy g ty

let isFSharpDelegateTy g ty = isDelegateTy g ty && isFSharpObjModelTy g ty

let isClassTy g ty = 
    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> info.IsClass
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> td.IsClass
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> isFSharpClassTy g ty

let isStructOrEnumTyconTy g ty = 
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.Deref.IsStructOrEnumTycon
    | _ -> false

let isStructRecordOrUnionTyconTy g ty = 
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.Deref.IsStructRecordOrUnionTycon
    | _ -> false

let isStructTyconRef (tcref: TyconRef) =
    let tycon = tcref.Deref
    tycon.IsStructRecordOrUnionTycon || tycon.IsStructOrEnumTycon

let isStructTy g ty =
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> 
        isStructTyconRef tcref
    | _ -> 
        isStructAnonRecdTy g ty || isStructTupleTy g ty

let isRefTy g ty = 
    not (isStructOrEnumTyconTy g ty) &&
    (
        isUnionTy g ty || 
        isRefTupleTy g ty || 
        isRecdTy g ty || 
        isILReferenceTy g ty ||
        isFunTy g ty || 
        isReprHiddenTy g ty || 
        isFSharpObjModelRefTy g ty || 
        isUnitTy g ty ||
        (isAnonRecdTy g ty && not (isStructAnonRecdTy g ty))
    )

let isForallFunctionTy g ty =
    let _, tau = tryDestForallTy g ty
    isFunTy g tau

// An unmanaged-type is any type that isn't a reference-type, a type-parameter, or a generic struct-type and
// contains no fields whose type is not an unmanaged-type. In other words, an unmanaged-type is one of the
// following:
// - sbyte, byte, short, ushort, int, uint, long, ulong, char, float, double, decimal, or bool.
// - Any enum-type.
// - Any pointer-type.
// - Any generic user-defined struct-type that can be statically determined to be 'unmanaged' at construction.
let rec isUnmanagedTy g ty =
    let isUnmanagedRecordField tinst rf  =
        isUnmanagedTy g (actualTyOfRecdField tinst rf)

    let ty = stripTyEqnsAndMeasureEqns g ty
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref ->
        let isEq tcref2 = tyconRefEq g tcref tcref2
        if isEq g.nativeptr_tcr || isEq g.nativeint_tcr ||
                    isEq g.sbyte_tcr || isEq g.byte_tcr ||
                    isEq g.int16_tcr || isEq g.uint16_tcr ||
                    isEq g.int32_tcr || isEq g.uint32_tcr ||
                    isEq g.int64_tcr || isEq g.uint64_tcr ||
                    isEq g.char_tcr || isEq g.voidptr_tcr ||
                    isEq g.float32_tcr ||
                    isEq g.float_tcr ||
                    isEq g.decimal_tcr ||
                    isEq g.bool_tcr then
            true
        else
            let tycon = tcref.Deref
            if tycon.IsEnumTycon then
                true
            elif isStructUnionTy g ty then
                let tinst = mkInstForAppTy g ty 
                tcref.UnionCasesAsRefList            
                |> List.forall (fun c -> c |> actualTysOfUnionCaseFields tinst |> List.forall (isUnmanagedTy g))
            elif tycon.IsStructOrEnumTycon then
                let tinst = mkInstForAppTy g ty
                tycon.AllInstanceFieldsAsList 
                |> List.forall (isUnmanagedRecordField tinst)
            else false
    | ValueNone ->
        if isStructTupleTy g ty then
            (destStructTupleTy g ty) |> List.forall (isUnmanagedTy g)
        else if isStructAnonRecdTy g ty then
            (destStructAnonRecdTy g ty) |> List.forall (isUnmanagedTy g)
        else
            false

let isInterfaceTycon x = 
    isILInterfaceTycon x || x.IsFSharpInterfaceTycon

let isInterfaceTyconRef (tcref: TyconRef) = isInterfaceTycon tcref.Deref

let isEnumTy g ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tcref.IsEnumTycon

let isSignedIntegerTy g ty =
    typeEquivAux EraseMeasures g g.sbyte_ty ty || 
    typeEquivAux EraseMeasures g g.int16_ty ty || 
    typeEquivAux EraseMeasures g g.int32_ty ty || 
    typeEquivAux EraseMeasures g g.nativeint_ty ty || 
    typeEquivAux EraseMeasures g g.int64_ty ty 

let isUnsignedIntegerTy g ty =
    typeEquivAux EraseMeasures g g.byte_ty ty || 
    typeEquivAux EraseMeasures g g.uint16_ty ty || 
    typeEquivAux EraseMeasures g g.uint32_ty ty || 
    typeEquivAux EraseMeasures g g.unativeint_ty ty || 
    typeEquivAux EraseMeasures g g.uint64_ty ty 

let isIntegerTy g ty =
    isSignedIntegerTy g ty || 
    isUnsignedIntegerTy g ty

/// float or float32 or float<_> or float32<_> 
let isFpTy g ty =
    typeEquivAux EraseMeasures g g.float_ty ty || 
    typeEquivAux EraseMeasures g g.float32_ty ty 

/// decimal or decimal<_>
let isDecimalTy g ty = 
    typeEquivAux EraseMeasures g g.decimal_ty ty

let isNonDecimalNumericType g ty = isIntegerTy g ty || isFpTy g ty

let isNumericType g ty = isNonDecimalNumericType g ty || isDecimalTy g ty

let actualReturnTyOfSlotSig parentTyInst methTyInst (TSlotSig(_, _, parentFormalTypars, methFormalTypars, _, formalRetTy)) = 
    let methTyInst = mkTyparInst methFormalTypars methTyInst
    let parentTyInst = mkTyparInst parentFormalTypars parentTyInst
    Option.map (instType (parentTyInst @ methTyInst)) formalRetTy

let slotSigHasVoidReturnTy (TSlotSig(_, _, _, _, _, formalRetTy)) = 
    Option.isNone formalRetTy 

let returnTyOfMethod g (TObjExprMethod(TSlotSig(_, parentTy, _, _, _, _) as ss, _, methFormalTypars, _, _, _)) =
    let tinst = argsOfAppTy g parentTy
    let methTyInst = generalizeTypars methFormalTypars
    actualReturnTyOfSlotSig tinst methTyInst ss

/// Is the type 'abstract' in C#-speak
let isAbstractTycon (tycon: Tycon) = 
    if tycon.IsFSharpObjectModelTycon then 
        not tycon.IsFSharpDelegateTycon && 
        tycon.TypeContents.tcaug_abstract 
    else 
        tycon.IsILTycon && tycon.ILTyconRawMetadata.IsAbstract

//---------------------------------------------------------------------------
// Determine if a member/Val/ValRef is an explicit impl
//---------------------------------------------------------------------------

let MemberIsExplicitImpl g (membInfo: ValMemberInfo) = 
   membInfo.MemberFlags.IsOverrideOrExplicitImpl &&
   match membInfo.ImplementedSlotSigs with 
   | [] -> false
   | slotsigs -> slotsigs |> List.forall (fun slotsig -> isInterfaceTy g slotsig.DeclaringType)

let ValIsExplicitImpl g (v: Val) = 
    match v.MemberInfo with 
    | Some membInfo -> MemberIsExplicitImpl g membInfo
    | _ -> false

let ValRefIsExplicitImpl g (vref: ValRef) = ValIsExplicitImpl g vref.Deref

//---------------------------------------------------------------------------
// Find all type variables in a type, apart from those that have had 
// an equation assigned by type inference.
//---------------------------------------------------------------------------

let emptyFreeLocals = Zset.empty valOrder
let unionFreeLocals s1 s2 = 
    if s1 === emptyFreeLocals then s2
    elif s2 === emptyFreeLocals then s1
    else Zset.union s1 s2

let emptyFreeRecdFields = Zset.empty recdFieldRefOrder
let unionFreeRecdFields s1 s2 = 
    if s1 === emptyFreeRecdFields then s2
    elif s2 === emptyFreeRecdFields then s1
    else Zset.union s1 s2

let emptyFreeUnionCases = Zset.empty unionCaseRefOrder
let unionFreeUnionCases s1 s2 = 
    if s1 === emptyFreeUnionCases then s2
    elif s2 === emptyFreeUnionCases then s1
    else Zset.union s1 s2

let emptyFreeTycons = Zset.empty tyconOrder
let unionFreeTycons s1 s2 = 
    if s1 === emptyFreeTycons then s2
    elif s2 === emptyFreeTycons then s1
    else Zset.union s1 s2

let typarOrder = 
    { new IComparer<Typar> with 
        member x.Compare (v1: Typar, v2: Typar) = compareBy v1 v2 _.Stamp } 

let emptyFreeTypars = Zset.empty typarOrder
let unionFreeTypars s1 s2 = 
    if s1 === emptyFreeTypars then s2
    elif s2 === emptyFreeTypars then s1
    else Zset.union s1 s2

let emptyFreeTyvars =  
    { FreeTycons = emptyFreeTycons
      // The summary of values used as trait solutions
      FreeTraitSolutions = emptyFreeLocals
      FreeTypars = emptyFreeTypars }

let isEmptyFreeTyvars ftyvs = 
    Zset.isEmpty ftyvs.FreeTypars &&
    Zset.isEmpty ftyvs.FreeTycons 

let unionFreeTyvars fvs1 fvs2 = 
    if fvs1 === emptyFreeTyvars then fvs2 else 
    if fvs2 === emptyFreeTyvars then fvs1 else
    { FreeTycons = unionFreeTycons fvs1.FreeTycons fvs2.FreeTycons
      FreeTraitSolutions = unionFreeLocals fvs1.FreeTraitSolutions fvs2.FreeTraitSolutions
      FreeTypars = unionFreeTypars fvs1.FreeTypars fvs2.FreeTypars }

type FreeVarOptions = 
    { canCache: bool
      collectInTypes: bool
      includeLocalTycons: bool
      includeTypars: bool
      includeLocalTyconReprs: bool
      includeRecdFields: bool
      includeUnionCases: bool
      includeLocals: bool
      templateReplacement: ((TyconRef -> bool) * Typars) option
      stackGuard: StackGuard option }

    member this.WithTemplateReplacement(f, typars) = { this with templateReplacement = Some (f, typars) }
      
let CollectAllNoCaching = 
    { canCache = false
      collectInTypes = true
      includeLocalTycons = true
      includeLocalTyconReprs = true
      includeRecdFields = true
      includeUnionCases = true
      includeTypars = true
      includeLocals = true
      templateReplacement = None
      stackGuard = None}

let CollectTyparsNoCaching = 
    { canCache = false
      collectInTypes = true
      includeLocalTycons = false
      includeTypars = true
      includeLocalTyconReprs = false
      includeRecdFields = false
      includeUnionCases = false
      includeLocals = false
      templateReplacement = None
      stackGuard = None }

let CollectLocalsNoCaching = 
    { canCache = false
      collectInTypes = false
      includeLocalTycons = false
      includeTypars = false
      includeLocalTyconReprs = false
      includeRecdFields = false 
      includeUnionCases = false
      includeLocals = true
      templateReplacement = None
      stackGuard = None }

let CollectTyparsAndLocalsNoCaching = 
    { canCache = false
      collectInTypes = true
      includeLocalTycons = false
      includeLocalTyconReprs = false
      includeRecdFields = false 
      includeUnionCases = false
      includeTypars = true
      includeLocals = true
      templateReplacement = None
      stackGuard = None }

let CollectAll =
    { canCache = false
      collectInTypes = true
      includeLocalTycons = true
      includeLocalTyconReprs = true
      includeRecdFields = true 
      includeUnionCases = true
      includeTypars = true
      includeLocals = true
      templateReplacement = None
      stackGuard = None }
    
let CollectTyparsAndLocalsImpl stackGuardOpt = // CollectAll
    { canCache = true // only cache for this one
      collectInTypes = true
      includeTypars = true
      includeLocals = true
      includeLocalTycons = false
      includeLocalTyconReprs = false
      includeRecdFields = false
      includeUnionCases = false
      templateReplacement = None
      stackGuard = stackGuardOpt }

  
let CollectTyparsAndLocals = CollectTyparsAndLocalsImpl None

let CollectTypars = CollectTyparsAndLocals

let CollectLocals = CollectTyparsAndLocals

let CollectTyparsAndLocalsWithStackGuard() =
    let stackGuard = StackGuard(AccFreeVarsStackGuardDepth, "AccFreeVarsStackGuardDepth")
    CollectTyparsAndLocalsImpl (Some stackGuard)

let CollectLocalsWithStackGuard() = CollectTyparsAndLocalsWithStackGuard()

let accFreeLocalTycon opts x acc = 
    if not opts.includeLocalTycons then acc else
    if Zset.contains x acc.FreeTycons then acc else 
    { acc with FreeTycons = Zset.add x acc.FreeTycons } 

let rec accFreeTycon opts (tcref: TyconRef) acc = 
    let acc =
        match opts.templateReplacement with
        | Some (isTemplateTyconRef, cloFreeTyvars) when isTemplateTyconRef tcref ->
            let cloInst = List.map mkTyparTy cloFreeTyvars
            accFreeInTypes opts cloInst acc
        | _ -> acc
    if not opts.includeLocalTycons then acc
    elif tcref.IsLocalRef then accFreeLocalTycon opts tcref.ResolvedTarget acc
    else acc

and boundTypars opts tps acc = 
    // Bound type vars form a recursively-referential set due to constraints, e.g. A: I<B>, B: I<A> 
    // So collect up free vars in all constraints first, then bind all variables 
    let acc = List.foldBack (fun (tp: Typar) acc -> accFreeInTyparConstraints opts tp.Constraints acc) tps acc
    List.foldBack (fun tp acc -> { acc with FreeTypars = Zset.remove tp acc.FreeTypars}) tps acc

and accFreeInTyparConstraints opts cxs acc =
    List.foldBack (accFreeInTyparConstraint opts) cxs acc

and accFreeInTyparConstraint opts tpc acc =
    match tpc with 
    | TyparConstraint.CoercesTo(ty, _) -> accFreeInType opts ty acc
    | TyparConstraint.MayResolveMember (traitInfo, _) -> accFreeInTrait opts traitInfo acc
    | TyparConstraint.DefaultsTo(_, defaultTy, _) -> accFreeInType opts defaultTy acc
    | TyparConstraint.SimpleChoice(tys, _) -> accFreeInTypes opts tys acc
    | TyparConstraint.IsEnum(underlyingTy, _) -> accFreeInType opts underlyingTy acc
    | TyparConstraint.IsDelegate(argTys, retTy, _) -> accFreeInType opts argTys (accFreeInType opts retTy acc)
    | TyparConstraint.SupportsComparison _
    | TyparConstraint.SupportsEquality _
    | TyparConstraint.SupportsNull _ 
    | TyparConstraint.NotSupportsNull _ 
    | TyparConstraint.IsNonNullableStruct _ 
    | TyparConstraint.IsReferenceType _ 
    | TyparConstraint.IsUnmanaged _
    | TyparConstraint.AllowsRefStruct _
    | TyparConstraint.RequiresDefaultConstructor _ -> acc

and accFreeInTrait opts (TTrait(tys, _, _, argTys, retTy, _, sln)) acc = 
    Option.foldBack (accFreeInTraitSln opts) sln.Value
       (accFreeInTypes opts tys 
         (accFreeInTypes opts argTys 
           (Option.foldBack (accFreeInType opts) retTy acc)))

and accFreeInTraitSln opts sln acc = 
    match sln with 
    | ILMethSln(ty, _, _, minst, staticTyOpt) ->
        Option.foldBack (accFreeInType opts) staticTyOpt
            (accFreeInType opts ty 
                (accFreeInTypes opts minst acc))
    | FSMethSln(ty, vref, minst, staticTyOpt) ->
        Option.foldBack (accFreeInType opts) staticTyOpt
         (accFreeInType opts ty 
            (accFreeValRefInTraitSln opts vref  
               (accFreeInTypes opts minst acc)))
    | FSAnonRecdFieldSln(_anonInfo, tinst, _n) ->
         accFreeInTypes opts tinst acc
    | FSRecdFieldSln(tinst, _rfref, _isSet) ->
         accFreeInTypes opts tinst acc
    | BuiltInSln -> acc
    | ClosedExprSln _ -> acc // nothing to accumulate because it's a closed expression referring only to erasure of provided method calls

and accFreeLocalValInTraitSln _opts v fvs =
    if Zset.contains v fvs.FreeTraitSolutions then fvs 
    else { fvs with FreeTraitSolutions = Zset.add v fvs.FreeTraitSolutions}

and accFreeValRefInTraitSln opts (vref: ValRef) fvs = 
    if vref.IsLocalRef then
        accFreeLocalValInTraitSln opts vref.ResolvedTarget fvs
    else
        // non-local values do not contain free variables 
        fvs

and accFreeTyparRef opts (tp: Typar) acc = 
    if not opts.includeTypars then acc else
    if Zset.contains tp acc.FreeTypars then acc 
    else 
        accFreeInTyparConstraints opts tp.Constraints
          { acc with FreeTypars = Zset.add tp acc.FreeTypars}

and accFreeInType opts ty acc = 
    match stripTyparEqns ty with 
    | TType_tuple (tupInfo, l) ->
        accFreeInTypes opts l (accFreeInTupInfo opts tupInfo acc)

    | TType_anon (anonInfo, l) ->
        accFreeInTypes opts l (accFreeInTupInfo opts anonInfo.TupInfo acc)

    | TType_app (tcref, tinst, _) -> 
        let acc = accFreeTycon opts tcref acc
        match tinst with 
        | [] -> acc  // optimization to avoid unneeded call
        | [h] -> accFreeInType opts h acc // optimization to avoid unneeded call
        | _ -> accFreeInTypes opts tinst acc

    | TType_ucase (UnionCaseRef(tcref, _), tinst) ->
        accFreeInTypes opts tinst (accFreeTycon opts tcref acc)

    | TType_fun (domainTy, rangeTy, _) ->
        accFreeInType opts domainTy (accFreeInType opts rangeTy acc)

    | TType_var (r, _) ->
        accFreeTyparRef opts r acc

    | TType_forall (tps, r) ->
        unionFreeTyvars (boundTypars opts tps (freeInType opts r)) acc

    | TType_measure unt -> accFreeInMeasure opts unt acc

and accFreeInTupInfo _opts unt acc = 
    match unt with 
    | TupInfo.Const _ -> acc
and accFreeInMeasure opts unt acc = List.foldBack (fun (tp, _) acc -> accFreeTyparRef opts tp acc) (ListMeasureVarOccsWithNonZeroExponents unt) acc
and accFreeInTypes opts tys acc = 
    match tys with 
    | [] -> acc
    | h :: t -> accFreeInTypes opts t (accFreeInType opts h acc)
and freeInType opts ty = accFreeInType opts ty emptyFreeTyvars

and accFreeInVal opts (v: Val) acc = accFreeInType opts v.val_type acc

let freeInTypes opts tys = accFreeInTypes opts tys emptyFreeTyvars
let freeInVal opts v = accFreeInVal opts v emptyFreeTyvars
let freeInTyparConstraints opts v = accFreeInTyparConstraints opts v emptyFreeTyvars
let accFreeInTypars opts tps acc = List.foldBack (accFreeTyparRef opts) tps acc
        
let rec addFreeInModuleTy (mtyp: ModuleOrNamespaceType) acc =
    QueueList.foldBack (typeOfVal >> accFreeInType CollectAllNoCaching) mtyp.AllValsAndMembers
      (QueueList.foldBack (fun (mspec: ModuleOrNamespace) acc -> addFreeInModuleTy mspec.ModuleOrNamespaceType acc) mtyp.AllEntities acc)

let freeInModuleTy mtyp = addFreeInModuleTy mtyp emptyFreeTyvars


//--------------------------------------------------------------------------
// Free in type, left-to-right order preserved. This is used to determine the
// order of type variables for top-level definitions based on their signature, 
// so be careful not to change the order. We accumulate in reverse
// order.
//--------------------------------------------------------------------------

let emptyFreeTyparsLeftToRight = []
let unionFreeTyparsLeftToRight fvs1 fvs2 = ListSet.unionFavourRight typarEq fvs1 fvs2

let rec boundTyparsLeftToRight g cxFlag thruFlag acc tps = 
    // Bound type vars form a recursively-referential set due to constraints, e.g. A: I<B>, B: I<A> 
    // So collect up free vars in all constraints first, then bind all variables 
    List.fold (fun acc (tp: Typar) -> accFreeInTyparConstraintsLeftToRight g cxFlag thruFlag acc tp.Constraints) tps acc

and accFreeInTyparConstraintsLeftToRight g cxFlag thruFlag acc cxs =
    List.fold (accFreeInTyparConstraintLeftToRight g cxFlag thruFlag) acc cxs 

and accFreeInTyparConstraintLeftToRight g cxFlag thruFlag acc tpc =
    match tpc with 
    | TyparConstraint.CoercesTo(ty, _) ->
        accFreeInTypeLeftToRight g cxFlag thruFlag acc ty 
    | TyparConstraint.MayResolveMember (traitInfo, _) ->
        accFreeInTraitLeftToRight g cxFlag thruFlag acc traitInfo 
    | TyparConstraint.DefaultsTo(_, defaultTy, _) ->
        accFreeInTypeLeftToRight g cxFlag thruFlag acc defaultTy 
    | TyparConstraint.SimpleChoice(tys, _) ->
        accFreeInTypesLeftToRight g cxFlag thruFlag acc tys 
    | TyparConstraint.IsEnum(underlyingTy, _) ->
        accFreeInTypeLeftToRight g cxFlag thruFlag acc underlyingTy
    | TyparConstraint.IsDelegate(argTys, retTy, _) ->
        accFreeInTypeLeftToRight g cxFlag thruFlag (accFreeInTypeLeftToRight g cxFlag thruFlag acc argTys) retTy  
    | TyparConstraint.SupportsComparison _ 
    | TyparConstraint.SupportsEquality _ 
    | TyparConstraint.SupportsNull _ 
    | TyparConstraint.NotSupportsNull _ 
    | TyparConstraint.IsNonNullableStruct _ 
    | TyparConstraint.IsUnmanaged _
    | TyparConstraint.AllowsRefStruct _
    | TyparConstraint.IsReferenceType _ 
    | TyparConstraint.RequiresDefaultConstructor _ -> acc

and accFreeInTraitLeftToRight g cxFlag thruFlag acc (TTrait(tys, _, _, argTys, retTy, _, _)) = 
    let acc = accFreeInTypesLeftToRight g cxFlag thruFlag acc tys
    let acc = accFreeInTypesLeftToRight g cxFlag thruFlag acc argTys
    let acc = Option.fold (accFreeInTypeLeftToRight g cxFlag thruFlag) acc retTy
    acc

and accFreeTyparRefLeftToRight g cxFlag thruFlag acc (tp: Typar) = 
    if ListSet.contains typarEq tp acc then 
        acc
    else 
        let acc = ListSet.insert typarEq tp acc
        if cxFlag then 
            accFreeInTyparConstraintsLeftToRight g cxFlag thruFlag acc tp.Constraints
        else 
            acc

and accFreeInTypeLeftToRight g cxFlag thruFlag acc ty = 
    match (if thruFlag then stripTyEqns g ty else stripTyparEqns ty) with 
    | TType_anon (anonInfo, anonTys) ->
        let acc = accFreeInTupInfoLeftToRight g cxFlag thruFlag acc anonInfo.TupInfo 
        accFreeInTypesLeftToRight g cxFlag thruFlag acc anonTys 

    | TType_tuple (tupInfo, tupTys) -> 
        let acc = accFreeInTupInfoLeftToRight g cxFlag thruFlag acc tupInfo 
        accFreeInTypesLeftToRight g cxFlag thruFlag acc tupTys 

    | TType_app (_, tinst, _) ->
        accFreeInTypesLeftToRight g cxFlag thruFlag acc tinst 

    | TType_ucase (_, tinst) -> 
        accFreeInTypesLeftToRight g cxFlag thruFlag acc tinst 

    | TType_fun (domainTy, rangeTy, _) -> 
        let dacc = accFreeInTypeLeftToRight g cxFlag thruFlag acc domainTy 
        accFreeInTypeLeftToRight g cxFlag thruFlag dacc rangeTy

    | TType_var (r, _) -> 
        accFreeTyparRefLeftToRight g cxFlag thruFlag acc r 

    | TType_forall (tps, r) -> 
        let racc = accFreeInTypeLeftToRight g cxFlag thruFlag emptyFreeTyparsLeftToRight r
        unionFreeTyparsLeftToRight (boundTyparsLeftToRight g cxFlag thruFlag tps racc) acc

    | TType_measure unt ->
        let mvars = ListMeasureVarOccsWithNonZeroExponents unt
        List.foldBack (fun (tp, _) acc -> accFreeTyparRefLeftToRight g cxFlag thruFlag acc tp) mvars acc

and accFreeInTupInfoLeftToRight _g _cxFlag _thruFlag acc unt = 
    match unt with 
    | TupInfo.Const _ -> acc

and accFreeInTypesLeftToRight g cxFlag thruFlag acc tys = 
    match tys with 
    | [] -> acc
    | h :: t -> accFreeInTypesLeftToRight g cxFlag thruFlag (accFreeInTypeLeftToRight g cxFlag thruFlag acc h) t
    
let freeInTypeLeftToRight g thruFlag ty =
    accFreeInTypeLeftToRight g true thruFlag emptyFreeTyparsLeftToRight ty |> List.rev

let freeInTypesLeftToRight g thruFlag ty =
    accFreeInTypesLeftToRight g true thruFlag emptyFreeTyparsLeftToRight ty |> List.rev

let freeInTypesLeftToRightSkippingConstraints g ty =
    accFreeInTypesLeftToRight g false true emptyFreeTyparsLeftToRight ty |> List.rev

let valOfBind (b: Binding) = b.Var

let valsOfBinds (binds: Bindings) = binds |> List.map (fun b -> b.Var)

//--------------------------------------------------------------------------
// Values representing member functions on F# types
//--------------------------------------------------------------------------

// Pull apart the type for an F# value that represents an object model method. Do not strip off a 'unit' argument.
// Review: Should GetMemberTypeInFSharpForm have any other direct callers? 
let GetMemberTypeInFSharpForm g (memberFlags: SynMemberFlags) arities ty m = 
    let tps, argInfos, retTy, retInfo = GetValReprTypeInFSharpForm g arities ty m

    let argInfos = 
        if memberFlags.IsInstance then 
            match argInfos with
            | [] -> 
                errorR(InternalError("value does not have a valid member type", m))
                argInfos
            | _ :: t -> t
        else argInfos
    tps, argInfos, retTy, retInfo

// Check that an F# value represents an object model method. 
// It will also always have an arity (inferred from syntax). 
let checkMemberVal membInfo arity m =
    match membInfo, arity with 
    | None, _ -> error(InternalError("checkMemberVal - no membInfo", m))
    | _, None -> error(InternalError("checkMemberVal - no arity", m))
    | Some membInfo, Some arity -> (membInfo, arity)

let checkMemberValRef (vref: ValRef) =
    checkMemberVal vref.MemberInfo vref.ValReprInfo vref.Range
     
let GetFSharpViewOfReturnType (g: TcGlobals) retTy =
    match retTy with 
    | None -> g.unit_ty
    | Some retTy -> retTy

type TraitConstraintInfo with
    member traitInfo.GetReturnType(g: TcGlobals) =
        GetFSharpViewOfReturnType g traitInfo.CompiledReturnType

    member traitInfo.GetObjectType() =
        match traitInfo.MemberFlags.IsInstance, traitInfo.CompiledObjectAndArgumentTypes with
        | true, objTy :: _ ->
            Some objTy
        | _ ->
            None

    // For static property traits:
    //      ^T: (static member Zero: ^T)
    // The inner representation is 
    //      TraitConstraintInfo([^T], get_Zero, Property, Static, [], ^T)
    // and this returns
    //      []
    //
    // For the logically equivalent static get_property traits (i.e. the property as a get_ method)
    //      ^T: (static member get_Zero: unit -> ^T)
    // The inner representation is 
    //      TraitConstraintInfo([^T], get_Zero, Member, Static, [], ^T)
    // and this returns
    //      []
    //
    // For instance property traits
    //      ^T: (member Length: int)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Length, Property, Instance, [], int)
    // and this returns
    //      []
    //
    // For the logically equivalent instance get_property traits (i.e. the property as a get_ method)
    //      ^T: (member get_Length: unit -> int)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Length, Method, Instance, [^T], int)
    // and this returns
    //      []
    //
    // For index property traits
    //      ^T: (member Item: int -> int with get)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Item, Property, Instance, [^T; int], int)
    // and this returns
    //      [int]
    member traitInfo.GetCompiledArgumentTypes() =
        match traitInfo.MemberFlags.IsInstance, traitInfo.CompiledObjectAndArgumentTypes with
        | true, _ :: argTys ->
            argTys
        | _, argTys ->
            argTys

    // For static property traits:
    //      ^T: (static member Zero: ^T)
    // The inner representation is 
    //      TraitConstraintInfo([^T], get_Zero, PropertyGet, Static, [], ^T)
    // and this returns
    //      []
    //
    // For the logically equivalent static get_property traits (i.e. the property as a get_ method)
    //      ^T: (static member get_Zero: unit -> ^T)
    // The inner representation is 
    //      TraitConstraintInfo([^T], get_Zero, Member, Static, [], ^T)
    // and this returns
    //      [unit]
    //
    // For instance property traits
    //      ^T: (member Length: int)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Length, PropertyGet, Instance, [^T], int)
    // and this views the constraint as if it were
    //      []
    //
    // For the logically equivalent instance get_property traits (i.e. the property as a get_ method)
    //      ^T: (member get_Length: unit -> int)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Length, Member, Instance, [^T], int)
    // and this returns
    //      [unit]
    //
    // For index property traits
    //      (member Item: int -> int with get)
    // The inner TraitConstraintInfo representation is
    //      TraitConstraintInfo([^T], get_Item, PropertyGet, [^T; int], int)
    // and this returns
    //      [int]
    member traitInfo.GetLogicalArgumentTypes(g: TcGlobals) =
        match traitInfo.GetCompiledArgumentTypes(), traitInfo.MemberFlags.MemberKind with
        | [], SynMemberKind.Member -> [g.unit_ty]
        | argTys, _ -> argTys

    member traitInfo.MemberDisplayNameCore =
        let traitName0 = traitInfo.MemberLogicalName
        match traitInfo.MemberFlags.MemberKind with
        | SynMemberKind.PropertyGet
        | SynMemberKind.PropertySet ->
            match PrettyNaming.TryChopPropertyName traitName0 with
            | Some nm -> nm
            | None -> traitName0
        | _ -> traitName0

    /// Get the key associated with the member constraint.
    member traitInfo.GetWitnessInfo() =
        let (TTrait(tys, nm, memFlags, objAndArgTys, rty, _, _)) = traitInfo
        TraitWitnessInfo(tys, nm, memFlags, objAndArgTys, rty)

/// Get information about the trait constraints for a set of typars.
/// Put these in canonical order.
let GetTraitConstraintInfosOfTypars g (tps: Typars) = 
    [ for tp in tps do 
        for cx in tp.Constraints do
            match cx with 
            | TyparConstraint.MayResolveMember(traitInfo, _) -> traitInfo 
            | _ -> () ]
    |> ListSet.setify (traitsAEquiv g TypeEquivEnv.EmptyIgnoreNulls)
    |> List.sortBy (fun traitInfo -> traitInfo.MemberLogicalName, traitInfo.GetCompiledArgumentTypes().Length)

/// Get information about the runtime witnesses needed for a set of generalized typars
let GetTraitWitnessInfosOfTypars g numParentTypars typars = 
    let typs = typars |> List.skip numParentTypars
    let cxs = GetTraitConstraintInfosOfTypars g typs
    cxs |> List.map (fun cx -> cx.GetWitnessInfo())

/// Count the number of type parameters on the enclosing type
let CountEnclosingTyparsOfActualParentOfVal (v: Val) = 
    match v.ValReprInfo with 
    | None -> 0
    | Some _ -> 
        if v.IsExtensionMember then 0
        elif not v.IsMember then 0
        else v.MemberApparentEntity.TyparsNoRange.Length

let GetValReprTypeInCompiledForm g valReprInfo numEnclosingTypars ty m =
    let tps, paramArgInfos, retTy, retInfo = GetValReprTypeInFSharpForm g valReprInfo ty m
    let witnessInfos = GetTraitWitnessInfosOfTypars g numEnclosingTypars tps
    // Eliminate lone single unit arguments
    let paramArgInfos = 
        match paramArgInfos, valReprInfo.ArgInfos with 
        // static member and module value unit argument elimination
        | [[(_argType, _)]], [[]] -> 
            //assert isUnitTy g argType 
            [[]]
        // instance member unit argument elimination
        | [objInfo;[(_argType, _)]], [[_objArg];[]] -> 
            //assert isUnitTy g argType 
            [objInfo; []]
        | _ -> 
            paramArgInfos
    let retTy = if isUnitTy g retTy then None else Some retTy
    (tps, witnessInfos, paramArgInfos, retTy, retInfo)
     
// Pull apart the type for an F# value that represents an object model method
// and see the "member" form for the type, i.e. 
// detect methods with no arguments by (effectively) looking for single argument type of 'unit'. 
// The analysis is driven of the inferred arity information for the value.
//
// This is used not only for the compiled form - it's also used for all type checking and object model
// logic such as determining if abstract methods have been implemented or not, and how
// many arguments the method takes etc.
let GetMemberTypeInMemberForm g memberFlags valReprInfo numEnclosingTypars ty m =
    let tps, paramArgInfos, retTy, retInfo = GetMemberTypeInFSharpForm g memberFlags valReprInfo ty m
    let witnessInfos = GetTraitWitnessInfosOfTypars g numEnclosingTypars tps
    // Eliminate lone single unit arguments
    let paramArgInfos = 
        match paramArgInfos, valReprInfo.ArgInfos with 
        // static member and module value unit argument elimination
        | [[(argTy, _)]], [[]] -> 
            assert isUnitTy g argTy 
            [[]]
        // instance member unit argument elimination
        | [[(argTy, _)]], [[_objArg];[]] -> 
            assert isUnitTy g argTy 
            [[]]
        | _ -> 
            paramArgInfos
    let retTy = if isUnitTy g retTy then None else Some retTy
    (tps, witnessInfos, paramArgInfos, retTy, retInfo)

let GetTypeOfMemberInMemberForm g (vref: ValRef) =
    //assert (not vref.IsExtensionMember)
    let membInfo, valReprInfo = checkMemberValRef vref
    let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal vref.Deref
    GetMemberTypeInMemberForm g membInfo.MemberFlags valReprInfo numEnclosingTypars vref.Type vref.Range

let GetTypeOfMemberInFSharpForm g (vref: ValRef) =
    let membInfo, valReprInfo = checkMemberValRef vref
    GetMemberTypeInFSharpForm g membInfo.MemberFlags valReprInfo vref.Type vref.Range

let PartitionValTyparsForApparentEnclosingType g (v: Val) = 
    match v.ValReprInfo with 
    | None -> error(InternalError("PartitionValTypars: not a top value", v.Range))
    | Some arities -> 
        let fullTypars, _ = destTopForallTy g arities v.Type 
        let parent = v.MemberApparentEntity
        let parentTypars = parent.TyparsNoRange
        let nparentTypars = parentTypars.Length
        if nparentTypars <= fullTypars.Length then 
            let memberParentTypars, memberMethodTypars = List.splitAt nparentTypars fullTypars
            let memberToParentInst, tinst = mkTyparToTyparRenaming memberParentTypars parentTypars
            Some(parentTypars, memberParentTypars, memberMethodTypars, memberToParentInst, tinst)
        else None

/// Match up the type variables on an member value with the type 
/// variables on the apparent enclosing type
let PartitionValTypars g (v: Val) = 
     match v.ValReprInfo with 
     | None -> error(InternalError("PartitionValTypars: not a top value", v.Range))
     | Some arities -> 
         if v.IsExtensionMember then 
             let fullTypars, _ = destTopForallTy g arities v.Type 
             Some([], [], fullTypars, emptyTyparInst, [])
         else
             PartitionValTyparsForApparentEnclosingType g v

let PartitionValRefTypars g (vref: ValRef) = PartitionValTypars g vref.Deref 

/// Get the arguments for an F# value that represents an object model method 
let ArgInfosOfMemberVal g (v: Val) = 
    let membInfo, valReprInfo = checkMemberVal v.MemberInfo v.ValReprInfo v.Range
    let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
    let _, _, arginfos, _, _ = GetMemberTypeInMemberForm g membInfo.MemberFlags valReprInfo numEnclosingTypars v.Type v.Range
    arginfos

let ArgInfosOfMember g (vref: ValRef) = 
    ArgInfosOfMemberVal g vref.Deref

/// Get the property "type" (getter return type) for an F# value that represents a getter or setter
/// of an object model property.
let ReturnTypeOfPropertyVal g (v: Val) = 
    let membInfo, valReprInfo = checkMemberVal v.MemberInfo v.ValReprInfo v.Range
    match membInfo.MemberFlags.MemberKind with 
    | SynMemberKind.PropertySet ->
        let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
        let _, _, arginfos, _, _ = GetMemberTypeInMemberForm g membInfo.MemberFlags valReprInfo numEnclosingTypars v.Type v.Range
        if not arginfos.IsEmpty && not arginfos.Head.IsEmpty then
            arginfos.Head |> List.last |> fst 
        else
            error(Error(FSComp.SR.tastValueDoesNotHaveSetterType(), v.Range))
    | SynMemberKind.PropertyGet ->
        let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
        let _, _, _, retTy, _ = GetMemberTypeInMemberForm g membInfo.MemberFlags valReprInfo numEnclosingTypars v.Type v.Range
        GetFSharpViewOfReturnType g retTy
    | _ -> error(InternalError("ReturnTypeOfPropertyVal", v.Range))


/// Get the property arguments for an F# value that represents a getter or setter
/// of an object model property.
let ArgInfosOfPropertyVal g (v: Val) = 
    let membInfo, valReprInfo = checkMemberVal v.MemberInfo v.ValReprInfo v.Range
    match membInfo.MemberFlags.MemberKind with 
    | SynMemberKind.PropertyGet ->
        ArgInfosOfMemberVal g v |> List.concat
    | SynMemberKind.PropertySet ->
        let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
        let _, _, arginfos, _, _ = GetMemberTypeInMemberForm g membInfo.MemberFlags valReprInfo numEnclosingTypars v.Type v.Range
        if not arginfos.IsEmpty && not arginfos.Head.IsEmpty then
            arginfos.Head |> List.frontAndBack |> fst 
        else
            error(Error(FSComp.SR.tastValueDoesNotHaveSetterType(), v.Range))
    | _ -> 
        error(InternalError("ArgInfosOfPropertyVal", v.Range))

//---------------------------------------------------------------------------
// Generalize type constructors to types
//---------------------------------------------------------------------------

let generalTyconRefInst (tcref: TyconRef) = 
    generalizeTypars tcref.TyparsNoRange

let generalizeTyconRef (g: TcGlobals) tcref = 
    let tinst = generalTyconRefInst tcref
    tinst, TType_app(tcref, tinst, g.knownWithoutNull)

let generalizedTyconRef (g: TcGlobals) tcref = 
    let tinst = generalTyconRefInst tcref
    TType_app(tcref, tinst, g.knownWithoutNull)

let isTTyparCoercesToType tpc = 
    match tpc with 
    | TyparConstraint.CoercesTo _  -> true
    | _ -> false

//--------------------------------------------------------------------------
// Print Signatures/Types - prelude
//-------------------------------------------------------------------------- 

let prefixOfStaticReq s =
    match s with 
    | TyparStaticReq.None -> "'"
    | TyparStaticReq.HeadType -> "^"

let prefixOfInferenceTypar (typar: Typar) =  
  if typar.Rigidity <> TyparRigidity.Rigid then "_" else ""

//---------------------------------------------------------------------------
// Prettify: PrettyTyparNames/PrettifyTypes - make typar names human friendly
//---------------------------------------------------------------------------

type TyparConstraintsWithTypars = (Typar * TyparConstraint) list

module PrettyTypes =
    let newPrettyTypar (tp: Typar) nm = 
        Construct.NewTypar (tp.Kind, tp.Rigidity, SynTypar(ident(nm, tp.Range), tp.StaticReq, false), false, TyparDynamicReq.Yes, [], false, false)

    let NewPrettyTypars renaming tps names = 
        let niceTypars = List.map2 newPrettyTypar tps names
        let tl, _tt = mkTyparToTyparRenaming tps niceTypars in
        let renaming = renaming @ tl
        (tps, niceTypars) ||> List.iter2 (fun tp tpnice -> tpnice.SetConstraints (instTyparConstraints renaming tp.Constraints)) 
        niceTypars, renaming

    // We choose names for type parameters from 'a'..'t'
    // We choose names for unit-of-measure from 'u'..'z'
    // If we run off the end of these ranges, we use 'aX' for positive integer X or 'uX' for positive integer X
    // Finally, we skip any names already in use
    let NeedsPrettyTyparName (tp: Typar) = 
        tp.IsCompilerGenerated && 
        tp.ILName.IsNone && 
        (tp.typar_id.idText = unassignedTyparName) 

    let PrettyTyparNames pred alreadyInUse tps = 
        let rec choose (tps: Typar list) (typeIndex, measureIndex) acc = 
            match tps with
            | [] -> List.rev acc
            | tp :: tps ->
            

                // Use a particular name, possibly after incrementing indexes
                let useThisName (nm, typeIndex, measureIndex) = 
                    choose tps (typeIndex, measureIndex) (nm :: acc)

                // Give up, try again with incremented indexes
                let tryAgain (typeIndex, measureIndex) = 
                    choose (tp :: tps) (typeIndex, measureIndex) acc

                let tryName (nm, typeIndex, measureIndex) f = 
                    if List.contains nm alreadyInUse then 
                        f()
                    else
                        useThisName (nm, typeIndex, measureIndex)

                if pred tp then 
                    if NeedsPrettyTyparName tp then 
                        let typeIndex, measureIndex, baseName, letters, i = 
                          match tp.Kind with 
                          | TyparKind.Type -> (typeIndex+1, measureIndex, 'a', 20, typeIndex) 
                          | TyparKind.Measure -> (typeIndex, measureIndex+1, 'u', 6, measureIndex)
                        let nm = 
                           if i < letters then String.make 1 (char(int baseName + i)) 
                           else String.make 1 baseName + string (i-letters+1)
                        tryName (nm, typeIndex, measureIndex) (fun () -> 
                            tryAgain (typeIndex, measureIndex))

                    else
                        tryName (tp.Name, typeIndex, measureIndex) (fun () -> 
                            // Use the next index and append it to the natural name
                            let typeIndex, measureIndex, nm = 
                              match tp.Kind with 
                              | TyparKind.Type -> (typeIndex+1, measureIndex, tp.Name+ string typeIndex) 
                              | TyparKind.Measure -> (typeIndex, measureIndex+1, tp.Name+ string measureIndex)
                            tryName (nm, typeIndex, measureIndex) (fun () -> 
                                tryAgain (typeIndex, measureIndex)))
                else
                    useThisName (tp.Name, typeIndex, measureIndex)
                          
        choose tps (0, 0) []

    let AssignPrettyTyparNames typars prettyNames =
        (typars, prettyNames)
        ||> List.iter2 (fun tp nm -> 
            if NeedsPrettyTyparName tp  then 
                tp.typar_id <- ident (nm, tp.Range))  
    
    let PrettifyThings g foldTys mapTys things = 
        let ftps = foldTys (accFreeInTypeLeftToRight g true false) emptyFreeTyparsLeftToRight things
        let ftps = List.rev ftps
        let rec computeKeep (keep: Typars) change (tps: Typars) = 
            match tps with 
            | [] -> List.rev keep, List.rev change 
            | tp :: rest -> 
                if not (NeedsPrettyTyparName tp) && (not (keep |> List.exists (fun tp2 -> tp.Name = tp2.Name))) then
                    computeKeep (tp :: keep) change rest
                else 
                    computeKeep keep (tp :: change) rest
        let keep, change = computeKeep [] [] ftps
        
        let alreadyInUse = keep |> List.map (fun x -> x.Name)
        let names = PrettyTyparNames (fun x -> List.memq x change) alreadyInUse ftps

        let niceTypars, renaming = NewPrettyTypars [] ftps names 
        
        // strip universal types for printing
        let getTauStayTau ty = 
            match ty with
            | TType_forall (_, tau) -> tau
            | _ -> ty
        let tauThings = mapTys getTauStayTau things
                        
        let prettyThings = mapTys (instType renaming) tauThings
        let tpconstraints = niceTypars |> List.collect (fun tpnice -> List.map (fun tpc -> tpnice, tpc) tpnice.Constraints)

        prettyThings, tpconstraints

    let PrettifyType g x = PrettifyThings g id id x
    let PrettifyTypePair g x = PrettifyThings g (fun f -> foldPair (f, f)) (fun f -> mapPair (f, f)) x
    let PrettifyTypes g x = PrettifyThings g List.fold List.map x
    
    let PrettifyDiscriminantAndTypePairs g x = 
      let tys, cxs = (PrettifyThings g List.fold List.map (x |> List.map snd))
      List.zip (List.map fst x) tys, cxs
      
    let PrettifyCurriedTypes g x = PrettifyThings g (List.fold >> List.fold) List.mapSquared x
    let PrettifyCurriedSigTypes g x = PrettifyThings g (fun f -> foldPair (List.fold (List.fold f), f)) (fun f -> mapPair (List.mapSquared f, f)) x

    // Badly formed code may instantiate rigid declared typars to types.
    // Hence we double check here that the thing is really a type variable
    let safeDestAnyParTy orig g ty = match tryAnyParTy g ty with ValueNone -> orig | ValueSome x -> x

    let foldUncurriedArgInfos f z (x: UncurriedArgInfos) = List.fold (fold1Of2 f) z x
    let foldTypar f z (x: Typar) = foldOn mkTyparTy f z x
    let mapTypar g f (x: Typar) : Typar = (mkTyparTy >> f >> safeDestAnyParTy x g) x

    let foldTypars f z (x: Typars) = List.fold (foldTypar f) z x
    let mapTypars g f (x: Typars) : Typars = List.map (mapTypar g f) x

    let foldTyparInst f z (x: TyparInstantiation) = List.fold (foldPair (foldTypar f, f)) z x
    let mapTyparInst g f (x: TyparInstantiation) : TyparInstantiation = List.map (mapPair (mapTypar g f, f)) x

    let PrettifyInstAndTyparsAndType g x = 
        PrettifyThings g 
            (fun f -> foldTriple (foldTyparInst f, foldTypars f, f)) 
            (fun f-> mapTriple (mapTyparInst g f, mapTypars g f, f)) 
            x

    let PrettifyInstAndUncurriedSig g (x: TyparInstantiation * UncurriedArgInfos * TType) = 
        PrettifyThings g 
            (fun f -> foldTriple (foldTyparInst f, foldUncurriedArgInfos f, f)) 
            (fun f -> mapTriple (mapTyparInst g f, List.map (map1Of2 f), f))
            x

    let PrettifyInstAndCurriedSig g (x: TyparInstantiation * TTypes * CurriedArgInfos * TType) = 
        PrettifyThings g 
            (fun f -> foldQuadruple (foldTyparInst f, List.fold f, List.fold (List.fold (fold1Of2 f)), f)) 
            (fun f -> mapQuadruple (mapTyparInst g f, List.map f, List.mapSquared (map1Of2 f), f))
            x

    let PrettifyInstAndSig g x = 
        PrettifyThings g 
            (fun f -> foldTriple (foldTyparInst f, List.fold f, f))
            (fun f -> mapTriple (mapTyparInst g f, List.map f, f) )
            x

    let PrettifyInstAndTypes g x = 
        PrettifyThings g 
            (fun f -> foldPair (foldTyparInst f, List.fold f)) 
            (fun f -> mapPair (mapTyparInst g f, List.map f))
            x
 
    let PrettifyInstAndType g x = 
        PrettifyThings g 
            (fun f -> foldPair (foldTyparInst f, f)) 
            (fun f -> mapPair (mapTyparInst g f, f))
            x
 
    let PrettifyInst g x = 
        PrettifyThings g 
            (foldTyparInst) 
            (fun f -> mapTyparInst g f)
            x
 
module SimplifyTypes =

    // CAREFUL! This function does NOT walk constraints 
    let rec foldTypeButNotConstraints f z ty =
        let ty = stripTyparEqns ty 
        let z = f z ty
        match ty with
        | TType_forall (_, bodyTy) ->
            foldTypeButNotConstraints f z bodyTy

        | TType_app (_, tys, _) 
        | TType_ucase (_, tys) 
        | TType_anon (_, tys) 
        | TType_tuple (_, tys) ->
            List.fold (foldTypeButNotConstraints f) z tys

        | TType_fun (domainTy, rangeTy, _) ->
            foldTypeButNotConstraints f (foldTypeButNotConstraints f z domainTy) rangeTy

        | TType_var _ -> z

        | TType_measure _ -> z

    let incM x m =
        if Zmap.mem x m then Zmap.add x (1 + Zmap.find x m) m
        else Zmap.add x 1 m

    let accTyparCounts z ty =
        // Walk type to determine typars and their counts (for pprinting decisions) 
        (z, ty) ||> foldTypeButNotConstraints (fun z ty ->
            match ty with
            | TType_var (tp, _) when tp.Rigidity = TyparRigidity.Rigid -> incM tp z
            | _ -> z)

    let emptyTyparCounts = Zmap.empty typarOrder

    // print multiple fragments of the same type using consistent naming and formatting 
    let accTyparCountsMulti acc l = List.fold accTyparCounts acc l

    type TypeSimplificationInfo =
        { singletons: Typar Zset
          inplaceConstraints: Zmap<Typar, TType>
          postfixConstraints: (Typar * TyparConstraint) list }
          
    let typeSimplificationInfo0 = 
        { singletons = Zset.empty typarOrder
          inplaceConstraints = Zmap.empty typarOrder
          postfixConstraints = [] }

    let categorizeConstraints simplify m cxs =
        let singletons = if simplify then Zmap.chooseL (fun tp n -> if n = 1 then Some tp else None) m else []
        let singletons = Zset.addList singletons (Zset.empty typarOrder)
        // Here, singletons are typars that occur once in the type.
        // However, they may also occur in a type constraint.
        // If they do, they are really multiple occurrence - so we should remove them.
        let constraintTypars = (freeInTyparConstraints CollectTyparsNoCaching (List.map snd cxs)).FreeTypars
        let usedInTypeConstraint typar = Zset.contains typar constraintTypars
        let singletons = singletons |> Zset.filter (usedInTypeConstraint >> not) 
        // Here, singletons should really be used once 
        let inplace, postfix =
          cxs |> List.partition (fun (tp, tpc) -> 
            simplify &&
            isTTyparCoercesToType tpc && 
            Zset.contains tp singletons && 
            List.isSingleton tp.Constraints)
        let inplace = inplace |> List.map (function tp, TyparConstraint.CoercesTo(ty, _) -> tp, ty | _ -> failwith "not isTTyparCoercesToType")
        
        { singletons = singletons
          inplaceConstraints = Zmap.ofList typarOrder inplace
          postfixConstraints = postfix }

    let CollectInfo simplify tys cxs = 
        categorizeConstraints simplify (accTyparCountsMulti emptyTyparCounts tys) cxs 

//--------------------------------------------------------------------------
// Print Signatures/Types
//-------------------------------------------------------------------------- 

type GenericParameterStyle =
    | Implicit
    | Prefix
    | Suffix

[<NoEquality; NoComparison>]
type DisplayEnv = 
    { includeStaticParametersInTypeNames: bool
      openTopPathsSorted: InterruptibleLazy<string list list>
      openTopPathsRaw: string list list
      shortTypeNames: bool
      suppressNestedTypes: bool
      maxMembers: int option
      showObsoleteMembers: bool
      showHiddenMembers: bool
      showTyparBinding: bool 
      showInferenceTyparAnnotations: bool
      suppressInlineKeyword: bool
      suppressMutableKeyword: bool
      showMemberContainers: bool
      shortConstraints: bool
      useColonForReturnType: bool
      showAttributes: bool
      showCsharpCodeAnalysisAttributes: bool
      showOverrides: bool
      showStaticallyResolvedTyparAnnotations: bool
      showNullnessAnnotations: bool option
      abbreviateAdditionalConstraints: bool
      showTyparDefaultConstraints: bool
      showDocumentation: bool
      shrinkOverloads: bool
      printVerboseSignatures: bool
      escapeKeywordNames: bool
      g: TcGlobals
      contextAccessibility: Accessibility
      generatedValueLayout : Val -> Layout option
      genericParameterStyle: GenericParameterStyle }

    member x.SetOpenPaths paths = 
        { x with 
             openTopPathsSorted = (InterruptibleLazy(fun _ -> paths |> List.sortWith (fun p1 p2 -> -(compare p1 p2))))
             openTopPathsRaw = paths 
        }

    static member Empty tcGlobals = 
      { includeStaticParametersInTypeNames = false
        openTopPathsRaw = []
        openTopPathsSorted = notlazy []
        shortTypeNames = false
        suppressNestedTypes = false
        maxMembers = None
        showObsoleteMembers = false
        showHiddenMembers = false
        showTyparBinding = false
        showInferenceTyparAnnotations = false
        suppressInlineKeyword = true
        suppressMutableKeyword = false
        showMemberContainers = false
        showAttributes = false
        showCsharpCodeAnalysisAttributes = false
        showOverrides = true
        showStaticallyResolvedTyparAnnotations = true
        showNullnessAnnotations = None
        showDocumentation = false
        abbreviateAdditionalConstraints = false
        showTyparDefaultConstraints = false
        shortConstraints = false
        useColonForReturnType = false
        shrinkOverloads = true
        printVerboseSignatures = false
        escapeKeywordNames = false
        g = tcGlobals
        contextAccessibility = taccessPublic
        generatedValueLayout = (fun _ -> None)
        genericParameterStyle = GenericParameterStyle.Implicit }


    member denv.AddOpenPath path = 
        denv.SetOpenPaths (path :: denv.openTopPathsRaw)

    member denv.AddOpenModuleOrNamespace (modref: ModuleOrNamespaceRef) = 
        denv.AddOpenPath (fullCompPathOfModuleOrNamespace modref.Deref).DemangledPath

    member denv.AddAccessibility access =
        { denv with contextAccessibility = combineAccess denv.contextAccessibility access }

    member denv.UseGenericParameterStyle style =
        { denv with genericParameterStyle = style }

    static member InitialForSigFileGeneration g =
        let denv =
            { DisplayEnv.Empty g with
               showInferenceTyparAnnotations = true
               showHiddenMembers = true
               showObsoleteMembers = true
               showAttributes = true
               suppressInlineKeyword = false
               showDocumentation = true
               shrinkOverloads = false
               escapeKeywordNames = true
               includeStaticParametersInTypeNames = true }
        denv.SetOpenPaths
            [ FSharpLib.RootPath
              FSharpLib.CorePath
              CollectionsPath
              ControlPath
              (splitNamespace ExtraTopLevelOperatorsName) ]

let (+.+) s1 s2 = if String.IsNullOrEmpty(s1) then s2 else !!s1+"."+s2

let layoutOfPath p =
    sepListL SepL.dot (List.map (tagNamespace >> wordL) p)

let fullNameOfParentOfPubPath pp = 
    match pp with 
    | PubPath([| _ |]) -> ValueNone 
    | pp -> ValueSome(textOfPath pp.EnclosingPath)

let fullNameOfParentOfPubPathAsLayout pp = 
    match pp with 
    | PubPath([| _ |]) -> ValueNone 
    | pp -> ValueSome(layoutOfPath (Array.toList pp.EnclosingPath))

let fullNameOfPubPath (PubPath p) = textOfPath p
let fullNameOfPubPathAsLayout (PubPath p) = layoutOfPath (Array.toList p)

let fullNameOfParentOfNonLocalEntityRef (nlr: NonLocalEntityRef) = 
    if nlr.Path.Length < 2 then ValueNone
    else ValueSome (textOfPath nlr.EnclosingMangledPath) 

let fullNameOfParentOfNonLocalEntityRefAsLayout (nlr: NonLocalEntityRef) = 
    if nlr.Path.Length < 2 then ValueNone
    else ValueSome (layoutOfPath (List.ofArray nlr.EnclosingMangledPath)) 

let fullNameOfParentOfEntityRef eref = 
    match eref with 
    | ERefLocal x ->
         match x.PublicPath with 
         | None -> ValueNone
         | Some ppath -> fullNameOfParentOfPubPath ppath
    | ERefNonLocal nlr -> fullNameOfParentOfNonLocalEntityRef nlr

let fullNameOfParentOfEntityRefAsLayout eref = 
    match eref with 
    | ERefLocal x ->
         match x.PublicPath with 
         | None -> ValueNone
         | Some ppath -> fullNameOfParentOfPubPathAsLayout ppath
    | ERefNonLocal nlr -> fullNameOfParentOfNonLocalEntityRefAsLayout nlr

let fullNameOfEntityRef nmF xref = 
    match fullNameOfParentOfEntityRef xref with 
    | ValueNone -> nmF xref 
    | ValueSome pathText -> pathText +.+ nmF xref

let tagEntityRefName (xref: EntityRef) name =
    if xref.IsNamespace then tagNamespace name
    elif xref.IsModule then tagModule name
    elif xref.IsTypeAbbrev then tagAlias name
    elif xref.IsFSharpDelegateTycon then tagDelegate name
    elif xref.IsILEnumTycon || xref.IsFSharpEnumTycon then tagEnum name
    elif xref.IsStructOrEnumTycon then tagStruct name
    elif isInterfaceTyconRef xref then tagInterface name
    elif xref.IsUnionTycon then tagUnion name
    elif xref.IsRecordTycon then tagRecord name
    else tagClass name

let fullDisplayTextOfTyconRef (tcref: TyconRef) = 
    fullNameOfEntityRef (fun tcref -> tcref.DisplayNameWithStaticParametersAndUnderscoreTypars) tcref

let fullNameOfEntityRefAsLayout nmF (xref: EntityRef) =
    let navigableText = 
        tagEntityRefName xref (nmF xref)
        |> mkNav xref.DefinitionRange
        |> wordL
    match fullNameOfParentOfEntityRefAsLayout xref with 
    | ValueNone -> navigableText
    | ValueSome pathText -> pathText ^^ SepL.dot ^^ navigableText

let fullNameOfParentOfValRef vref = 
    match vref with 
    | VRefLocal x -> 
         match x.PublicPath with 
         | None -> ValueNone
         | Some (ValPubPath(pp, _)) -> ValueSome(fullNameOfPubPath pp)
    | VRefNonLocal nlr -> 
        ValueSome (fullNameOfEntityRef (fun (x: EntityRef) -> x.DemangledModuleOrNamespaceName) nlr.EnclosingEntity)

let fullNameOfParentOfValRefAsLayout vref = 
    match vref with 
    | VRefLocal x -> 
         match x.PublicPath with 
         | None -> ValueNone
         | Some (ValPubPath(pp, _)) -> ValueSome(fullNameOfPubPathAsLayout pp)
    | VRefNonLocal nlr -> 
        ValueSome (fullNameOfEntityRefAsLayout (fun (x: EntityRef) -> x.DemangledModuleOrNamespaceName) nlr.EnclosingEntity)

let fullDisplayTextOfParentOfModRef eref = fullNameOfParentOfEntityRef eref

let fullDisplayTextOfModRef r =
    fullNameOfEntityRef (fun eref -> eref.DemangledModuleOrNamespaceName) r

let fullDisplayTextOfTyconRefAsLayout tcref =
    fullNameOfEntityRefAsLayout (fun tcref -> tcref.DisplayNameWithStaticParametersAndUnderscoreTypars) tcref

let fullDisplayTextOfExnRef tcref =
    fullNameOfEntityRef (fun tcref -> tcref.DisplayNameWithStaticParametersAndUnderscoreTypars) tcref

let fullDisplayTextOfExnRefAsLayout tcref =
    fullNameOfEntityRefAsLayout (fun tcref -> tcref.DisplayNameWithStaticParametersAndUnderscoreTypars) tcref

let fullDisplayTextOfUnionCaseRef (ucref: UnionCaseRef) =
    fullDisplayTextOfTyconRef ucref.TyconRef +.+ ucref.CaseName

let fullDisplayTextOfRecdFieldRef (rfref: RecdFieldRef) =
    fullDisplayTextOfTyconRef rfref.TyconRef +.+ rfref.FieldName

let fullDisplayTextOfValRef (vref: ValRef) = 
    match fullNameOfParentOfValRef vref with 
    | ValueNone -> vref.DisplayName 
    | ValueSome pathText -> pathText +.+ vref.DisplayName

let fullDisplayTextOfValRefAsLayout (vref: ValRef) = 
    let n =
        match vref.MemberInfo with
        | None -> 
            if vref.IsModuleBinding then tagModuleBinding vref.DisplayName
            else tagUnknownEntity vref.DisplayName
        | Some memberInfo ->
            match memberInfo.MemberFlags.MemberKind with
            | SynMemberKind.PropertyGet
            | SynMemberKind.PropertySet
            | SynMemberKind.PropertyGetSet -> tagProperty vref.DisplayName
            | SynMemberKind.ClassConstructor
            | SynMemberKind.Constructor -> tagMethod vref.DisplayName
            | SynMemberKind.Member -> tagMember vref.DisplayName
    match fullNameOfParentOfValRefAsLayout vref with 
    | ValueNone -> wordL n 
    | ValueSome pathText -> 
        pathText ^^ SepL.dot ^^ wordL n
        //pathText +.+ vref.DisplayName

let fullMangledPathToTyconRef (tcref:TyconRef) = 
    match tcref with 
    | ERefLocal _ -> (match tcref.PublicPath with None -> [| |] | Some pp -> pp.EnclosingPath)
    | ERefNonLocal nlr -> nlr.EnclosingMangledPath
    
/// generates a name like 'System.IComparable<System.Int32>.Get'
let tyconRefToFullName (tcref:TyconRef) =
    let namespaceParts =
        // we need to ensure there are no collisions between (for example)
        // - ``IB<GlobalType>`` (non-generic)
        // - IB<'T> instantiated with 'T = GlobalType
        // This is only an issue for types inside the global namespace, because '.' is invalid even in a quoted identifier.
        // So if the type is in the global namespace, prepend 'global`', because '`' is also illegal -> there can be no quoted identifer with that name.
        match fullMangledPathToTyconRef tcref with
        | [||] -> [| "global`" |]
        | ns -> ns
    seq { yield! namespaceParts; yield tcref.DisplayName } |> String.concat "."

let rec qualifiedInterfaceImplementationNameAux g (x:TType) : string =
    match stripMeasuresFromTy g (stripTyEqnsAndErase true g x) with
    | TType_app (a, [], _) ->
        tyconRefToFullName a

    | TType_anon (a,b) ->
        let genericParameters = b |> Seq.map (qualifiedInterfaceImplementationNameAux g) |> String.concat ", "
        sprintf "%s<%s>" a.ILTypeRef.FullName genericParameters

    | TType_app (a, b, _) ->
        let genericParameters = b |> Seq.map (qualifiedInterfaceImplementationNameAux g) |> String.concat ", "
        sprintf "%s<%s>" (tyconRefToFullName a) genericParameters

    | TType_var (v, _) ->
        "'" + v.Name

    | _ ->
        failwithf "unexpected: expected TType_app but got %O" (x.GetType())

/// for types in the global namespace, `global is prepended (note the backtick)
let qualifiedInterfaceImplementationName g (ty: TType) memberName =
    let interfaceName = ty |> qualifiedInterfaceImplementationNameAux g
    sprintf "%s.%s" interfaceName memberName

let qualifiedMangledNameOfTyconRef tcref nm = 
    String.concat "-" (Array.toList (fullMangledPathToTyconRef tcref) @ [ tcref.LogicalName + "-" + nm ])

let rec firstEq p1 p2 = 
    match p1 with
    | [] -> true 
    | h1 :: t1 -> 
        match p2 with 
        | h2 :: t2 -> h1 = h2 && firstEq t1 t2
        | _ -> false 

let rec firstRem p1 p2 = 
   match p1 with [] -> p2 | _ :: t1 -> firstRem t1 (List.tail p2)

let trimPathByDisplayEnv denv path =
    let findOpenedNamespace openedPath = 
        if firstEq openedPath path then 
            let t2 = firstRem openedPath path
            if t2 <> [] then Some(textOfPath t2 + ".")
            else Some("")
        else None

    match List.tryPick findOpenedNamespace (denv.openTopPathsSorted.Force()) with
    | Some s -> s
    | None -> if isNil path then "" else textOfPath path + "."


let superOfTycon (g: TcGlobals) (tycon: Tycon) = 
    match tycon.TypeContents.tcaug_super with 
    | None -> g.obj_ty_noNulls 
    | Some ty -> ty 

/// walk a TyconRef's inheritance tree, yielding any parent types as an array
let supersOfTyconRef (tcref: TyconRef) =
    tcref |> Array.unfold (fun tcref ->
        match tcref.TypeContents.tcaug_super with
        | Some (TType_app(sup, _, _)) -> Some(sup, sup)
        | _ -> None)

//----------------------------------------------------------------------------
// Detect attributes
//----------------------------------------------------------------------------

// AbsIL view of attributes (we read these from .NET binaries) 
let isILAttribByName (tencl: string list, tname: string) (attr: ILAttribute) = 
    (attr.Method.DeclaringType.TypeSpec.Name = tname) &&
    (attr.Method.DeclaringType.TypeSpec.Enclosing = tencl)

// AbsIL view of attributes (we read these from .NET binaries). The comparison is done by name.
let isILAttrib (tref: ILTypeRef) (attr: ILAttribute) = 
    isILAttribByName (tref.Enclosing, tref.Name) attr

// REVIEW: consider supporting querying on Abstract IL custom attributes.
// These linear iterations cost us a fair bit when there are lots of attributes
// on imported types. However this is fairly rare and can also be solved by caching the
// results of attribute lookups in the TAST
let HasILAttribute tref (attrs: ILAttributes) = 
    attrs.AsArray() |> Array.exists (isILAttrib tref) 

let TryDecodeILAttribute tref (attrs: ILAttributes) = 
    attrs.AsArray() |> Array.tryPick (fun x -> if isILAttrib tref x then Some(decodeILAttribData x) else None)

// F# view of attributes (these get converted to AbsIL attributes in ilxgen) 
let IsMatchingFSharpAttribute g (AttribInfo(_, tcref)) (Attrib(tcref2, _, _, _, _, _, _)) = tyconRefEq g tcref tcref2
let HasFSharpAttribute g tref attrs = List.exists (IsMatchingFSharpAttribute g tref) attrs
let TryFindFSharpAttribute g tref attrs = List.tryFind (IsMatchingFSharpAttribute g tref) attrs
let TryFindFSharpAttributeOpt g tref attrs = match tref with None -> None | Some tref -> List.tryFind (IsMatchingFSharpAttribute g tref) attrs

let HasFSharpAttributeOpt g trefOpt attrs = match trefOpt with Some tref -> List.exists (IsMatchingFSharpAttribute g tref) attrs | _ -> false
let IsMatchingFSharpAttributeOpt g attrOpt (Attrib(tcref2, _, _, _, _, _, _)) = match attrOpt with Some (AttribInfo(_, tcref)) -> tyconRefEq g tcref tcref2 | _ -> false

[<return: Struct>]
let (|ExtractAttribNamedArg|_|) nm args = 
    args |> List.tryPick (function AttribNamedArg(nm2, _, _, v) when nm = nm2 -> Some v | _ -> None) |> ValueOption.ofOption

[<return: Struct>]
let (|ExtractILAttributeNamedArg|_|) nm (args: ILAttributeNamedArg list) = 
    args |> List.tryPick (function nm2, _, _, v when nm = nm2 -> Some v | _ -> None) |> ValueOption.ofOption

[<return: Struct>]
let (|StringExpr|_|) = function Expr.Const (Const.String n, _, _) -> ValueSome n | _ -> ValueNone

[<return: Struct>]
let (|AttribInt32Arg|_|) = function AttribExpr(_, Expr.Const (Const.Int32 n, _, _)) -> ValueSome n | _ -> ValueNone

[<return: Struct>]
let (|AttribInt16Arg|_|) = function AttribExpr(_, Expr.Const (Const.Int16 n, _, _)) -> ValueSome n | _ -> ValueNone

[<return: Struct>]
let (|AttribBoolArg|_|) = function AttribExpr(_, Expr.Const (Const.Bool n, _, _)) -> ValueSome n | _ -> ValueNone

[<return: Struct>]
let (|AttribStringArg|_|) = function AttribExpr(_, Expr.Const (Const.String n, _, _)) -> ValueSome n | _ -> ValueNone

let (|AttribElemStringArg|_|) = function ILAttribElem.String(n) -> n | _ -> None

let TryFindFSharpBoolAttributeWithDefault dflt g nm attrs = 
    match TryFindFSharpAttribute g nm attrs with
    | Some(Attrib(_, _, [ ], _, _, _, _)) -> Some dflt
    | Some(Attrib(_, _, [ AttribBoolArg b ], _, _, _, _)) -> Some b
    | _ -> None

let TryFindFSharpBoolAttribute g nm attrs = TryFindFSharpBoolAttributeWithDefault true g nm attrs
let TryFindFSharpBoolAttributeAssumeFalse g nm attrs = TryFindFSharpBoolAttributeWithDefault false g nm attrs

let TryFindFSharpInt32Attribute g nm attrs = 
    match TryFindFSharpAttribute g nm attrs with
    | Some(Attrib(_, _, [ AttribInt32Arg b ], _, _, _, _)) -> Some b
    | _ -> None
    
let TryFindFSharpStringAttribute g nm attrs = 
    match TryFindFSharpAttribute g nm attrs with
    | Some(Attrib(_, _, [ AttribStringArg b ], _, _, _, _)) -> Some b
    | _ -> None

let TryFindLocalizedFSharpStringAttribute g nm attrs = 
    match TryFindFSharpAttribute g nm attrs with
    | Some(Attrib(_, _, [ AttribStringArg b ], namedArgs, _, _, _)) -> 
        match namedArgs with 
        | ExtractAttribNamedArg "Localize" (AttribBoolArg true) -> FSComp.SR.GetTextOpt(b)
        | _ -> Some b
    | _ -> None
    
let TryFindILAttribute (AttribInfo (atref, _)) attrs = 
    HasILAttribute atref attrs

let TryFindILAttributeOpt attr attrs = 
    match attr with
    | Some (AttribInfo (atref, _)) -> HasILAttribute atref attrs
    | _ -> false

let IsILAttrib  (AttribInfo (builtInAttrRef, _)) attr = isILAttrib builtInAttrRef attr
    

/// Analyze three cases for attributes declared on type definitions: IL-declared attributes, F#-declared attributes and
/// provided attributes.
//
// This is used for AttributeUsageAttribute, DefaultMemberAttribute and ConditionalAttribute (on attribute types)
let TryBindTyconRefAttribute g (m: range) (AttribInfo (atref, _) as args) (tcref: TyconRef) f1 f2 (f3: obj option list * (string * obj option) list -> 'a option) : 'a option = 
    ignore m; ignore f3
    match metadataOfTycon tcref.Deref with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> 
        let provAttribs = info.ProvidedType.PApply((fun a -> (a :> IProvidedCustomAttributeProvider)), m)
        match provAttribs.PUntaint((fun a -> a.GetAttributeConstructorArgs(provAttribs.TypeProvider.PUntaintNoFailure id, atref.FullName)), m) with
        | Some args -> f3 args
        | None -> None
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, tdef)) -> 
        match TryDecodeILAttribute atref tdef.CustomAttrs with 
        | Some attr -> f1 attr
        | _ -> None
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> 
        match TryFindFSharpAttribute g args tcref.Attribs with 
        | Some attr -> f2 attr
        | _ -> None

let TryFindTyconRefBoolAttribute g m attribSpec tcref =
    TryBindTyconRefAttribute g m attribSpec tcref 
                (function 
                   | [ ], _ -> Some true
                   | [ILAttribElem.Bool v ], _ -> Some v 
                   | _ -> None)
                (function 
                   | Attrib(_, _, [ ], _, _, _, _) -> Some true
                   | Attrib(_, _, [ AttribBoolArg v ], _, _, _, _) -> Some v 
                   | _ -> None)
                (function 
                   | [ ], _ -> Some true
                   | [ Some (:? bool as v : obj) ], _ -> Some v 
                   | _ -> None)

/// Try to find the resolved attributeusage for an type by walking its inheritance tree and picking the correct attribute usage value
let TryFindAttributeUsageAttribute g m tcref =
    [| yield tcref
       yield! supersOfTyconRef tcref |]
    |> Array.tryPick (fun tcref ->
        TryBindTyconRefAttribute g m g.attrib_AttributeUsageAttribute tcref
                (fun (_, named) -> named |> List.tryPick (function "AllowMultiple", _, _, ILAttribElem.Bool res -> Some res | _ -> None))
                (fun (Attrib(_, _, _, named, _, _, _)) -> named |> List.tryPick (function AttribNamedArg("AllowMultiple", _, _, AttribBoolArg res ) -> Some res | _ -> None))
                (fun (_, named) -> named |> List.tryPick (function "AllowMultiple", Some (:? bool as res : obj) -> Some res | _ -> None))
    )

/// Try to find a specific attribute on a type definition, where the attribute accepts a string argument.
///
/// This is used to detect the 'DefaultMemberAttribute' and 'ConditionalAttribute' attributes (on type definitions)
let TryFindTyconRefStringAttribute g m attribSpec tcref =
    TryBindTyconRefAttribute g m attribSpec tcref 
                (function [ILAttribElem.String (Some msg) ], _ -> Some msg | _ -> None)
                (function Attrib(_, _, [ AttribStringArg msg ], _, _, _, _) -> Some msg | _ -> None)
                (function [ Some (:? string as msg : obj) ], _ -> Some msg | _ -> None)

/// Check if a type definition has a specific attribute
let TyconRefHasAttribute g m attribSpec tcref =
    TryBindTyconRefAttribute g m attribSpec tcref 
                    (fun _ -> Some ()) 
                    (fun _ -> Some ())
                    (fun _ -> Some ())
        |> Option.isSome

let HasDefaultAugmentationAttribute g (tcref: TyconRef) =
    match TryFindFSharpAttribute g g.attrib_DefaultAugmentationAttribute tcref.Attribs with
    | Some(Attrib(_, _, [ AttribBoolArg b ], _, _, _, _)) -> b
    | Some (Attrib(_, _, _, _, _, _, m)) ->
        errorR(Error(FSComp.SR.ilDefaultAugmentationAttributeCouldNotBeDecoded(), m))
        true
    | _ -> true

/// Check if a type definition has an attribute with a specific full name
let TyconRefHasAttributeByName (m: range) attrFullName (tcref: TyconRef) = 
    ignore m
    match metadataOfTycon tcref.Deref with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> 
        let provAttribs = info.ProvidedType.PApply((fun a -> (a :> IProvidedCustomAttributeProvider)), m)
        provAttribs.PUntaint((fun a ->
            a.GetAttributeConstructorArgs(provAttribs.TypeProvider.PUntaintNoFailure id, attrFullName)), m).IsSome
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, tdef)) ->
        tdef.CustomAttrs.AsArray()
        |> Array.exists (fun attr -> isILAttribByName ([], attrFullName) attr)
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata ->
        tcref.Attribs
        |> List.exists (fun attr ->
            match attr.TyconRef.CompiledRepresentation with
            | CompiledTypeRepr.ILAsmNamed(typeRef, _, _) ->
                typeRef.Enclosing.IsEmpty
                && typeRef.Name = attrFullName
            | CompiledTypeRepr.ILAsmOpen _ -> false)

let isByrefTyconRef (g: TcGlobals) (tcref: TyconRef) = 
    (g.byref_tcr.CanDeref && tyconRefEq g g.byref_tcr tcref) ||
    (g.byref2_tcr.CanDeref && tyconRefEq g g.byref2_tcr tcref) ||
    (g.inref_tcr.CanDeref && tyconRefEq g g.inref_tcr tcref) ||
    (g.outref_tcr.CanDeref && tyconRefEq g g.outref_tcr tcref) ||
    tyconRefEqOpt g g.system_TypedReference_tcref tcref ||
    tyconRefEqOpt g g.system_ArgIterator_tcref tcref ||
    tyconRefEqOpt g g.system_RuntimeArgumentHandle_tcref tcref

// See RFC FS-1053.md
let isByrefLikeTyconRef (g: TcGlobals) m (tcref: TyconRef) = 
    tcref.CanDeref &&
    match tcref.TryIsByRefLike with 
    | ValueSome res -> res
    | _ -> 
       let res = 
           isByrefTyconRef g tcref ||
           (isStructTyconRef tcref && TyconRefHasAttributeByName m tname_IsByRefLikeAttribute tcref)
       tcref.SetIsByRefLike res
       res

let isSpanLikeTyconRef g m tcref =
    isByrefLikeTyconRef g m tcref &&
    not (isByrefTyconRef g tcref)

let isByrefLikeTy g m ty = 
    ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> isByrefLikeTyconRef g m tcref | _ -> false)

let isSpanLikeTy g m ty =
    isByrefLikeTy g m ty && 
    not (isByrefTy g ty)

let isSpanTyconRef g m tcref =
    isByrefLikeTyconRef g m tcref &&
    tcref.CompiledRepresentationForNamedType.BasicQualifiedName = "System.Span`1"

let isSpanTy g m ty =
    ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> isSpanTyconRef g m tcref | _ -> false)

let tryDestSpanTy g m ty =
    match tryAppTy g ty with
    | ValueSome(tcref, [ty]) when isSpanTyconRef g m tcref -> Some(tcref, ty)
    | _ -> None

let destSpanTy g m ty =
    match tryDestSpanTy g m ty with
    | Some(tcref, ty) -> (tcref, ty)
    | _ -> failwith "destSpanTy"

let isReadOnlySpanTyconRef g m tcref =
    isByrefLikeTyconRef g m tcref &&
    tcref.CompiledRepresentationForNamedType.BasicQualifiedName = "System.ReadOnlySpan`1"

let isReadOnlySpanTy g m ty =
    ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> isReadOnlySpanTyconRef g m tcref | _ -> false)

let tryDestReadOnlySpanTy g m ty =
    match tryAppTy g ty with
    | ValueSome(tcref, [ty]) when isReadOnlySpanTyconRef g m tcref -> Some(tcref, ty)
    | _ -> None

let destReadOnlySpanTy g m ty =
    match tryDestReadOnlySpanTy g m ty with
    | Some(tcref, ty) -> (tcref, ty)
    | _ -> failwith "destReadOnlySpanTy"    

//-------------------------------------------------------------------------
// List and reference types...
//------------------------------------------------------------------------- 

let destByrefTy g ty = 
    match ty |> stripTyEqns g with
    | TType_app(tcref, [x; _], _) when g.byref2_tcr.CanDeref && tyconRefEq g g.byref2_tcr tcref -> x // Check sufficient FSharp.Core
    | TType_app(tcref, [x], _) when tyconRefEq g g.byref_tcr tcref -> x // all others
    | _ -> failwith "destByrefTy: not a byref type"

[<return: Struct>]
let (|ByrefTy|_|) g ty = 
    // Because of byref = byref2<ty,tags> it is better to write this using is/dest
    if isByrefTy g ty then ValueSome (destByrefTy g ty) else ValueNone

let destNativePtrTy g ty =
    match ty |> stripTyEqns g with
    | TType_app(tcref, [x], _) when tyconRefEq g g.nativeptr_tcr tcref -> x
    | _ -> failwith "destNativePtrTy: not a native ptr type"

let isRefCellTy g ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tyconRefEq g g.refcell_tcr_canon tcref

let destRefCellTy g ty = 
    match ty |> stripTyEqns g with
    | TType_app(tcref, [x], _) when tyconRefEq g g.refcell_tcr_canon tcref -> x
    | _ -> failwith "destRefCellTy: not a ref type"

let StripSelfRefCell(g: TcGlobals, baseOrThisInfo: ValBaseOrThisInfo, tau: TType) : TType =
    if baseOrThisInfo = CtorThisVal && isRefCellTy g tau 
        then destRefCellTy g tau 
        else tau

let mkRefCellTy (g: TcGlobals) ty = TType_app(g.refcell_tcr_nice, [ty], g.knownWithoutNull)

let mkLazyTy (g: TcGlobals) ty = TType_app(g.lazy_tcr_nice, [ty], g.knownWithoutNull)

let mkPrintfFormatTy (g: TcGlobals) aty bty cty dty ety = TType_app(g.format_tcr, [aty;bty;cty;dty; ety], g.knownWithoutNull)

let mkOptionTy (g: TcGlobals) ty = TType_app (g.option_tcr_nice, [ty], g.knownWithoutNull)

let mkValueOptionTy (g: TcGlobals) ty = TType_app (g.valueoption_tcr_nice, [ty], g.knownWithoutNull)

let mkNullableTy (g: TcGlobals) ty = TType_app (g.system_Nullable_tcref, [ty], g.knownWithoutNull)

let mkListTy (g: TcGlobals) ty = TType_app (g.list_tcr_nice, [ty], g.knownWithoutNull)

let isBoolTy (g: TcGlobals) ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> 
        tyconRefEq g g.system_Bool_tcref tcref ||
        tyconRefEq g g.bool_tcr tcref

let isValueOptionTy (g: TcGlobals) ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tyconRefEq g g.valueoption_tcr_canon tcref

let isOptionTy (g: TcGlobals) ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tyconRefEq g g.option_tcr_canon tcref

let isChoiceTy (g: TcGlobals) ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref ->
        tyconRefEq g g.choice2_tcr tcref ||
        tyconRefEq g g.choice3_tcr tcref ||
        tyconRefEq g g.choice4_tcr tcref ||
        tyconRefEq g g.choice5_tcr tcref ||
        tyconRefEq g g.choice6_tcr tcref ||
        tyconRefEq g g.choice7_tcr tcref

let tryDestOptionTy g ty = 
    match argsOfAppTy g ty with 
    | [ty1] when isOptionTy g ty -> ValueSome ty1
    | _ -> ValueNone

let tryDestValueOptionTy g ty = 
    match argsOfAppTy g ty with 
    | [ty1] when isValueOptionTy g ty -> ValueSome ty1
    | _ -> ValueNone

let tryDestChoiceTy g ty idx = 
    match argsOfAppTy g ty with 
    | ls when isChoiceTy g ty && ls.Length > idx -> ValueSome ls[idx]
    | _ -> ValueNone

let destOptionTy g ty = 
    match tryDestOptionTy g ty with 
    | ValueSome ty -> ty
    | ValueNone -> failwith "destOptionTy: not an option type"

let destValueOptionTy g ty = 
    match tryDestValueOptionTy g ty with 
    | ValueSome ty -> ty
    | ValueNone -> failwith "destValueOptionTy: not a value option type"

let destChoiceTy g ty idx = 
    match tryDestChoiceTy g ty idx with 
    | ValueSome ty -> ty
    | ValueNone -> failwith "destChoiceTy: not a Choice type"

let isNullableTy (g: TcGlobals) ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tyconRefEq g g.system_Nullable_tcref tcref

let tryDestNullableTy g ty = 
    match argsOfAppTy g ty with 
    | [ty1] when isNullableTy g ty -> ValueSome ty1
    | _ -> ValueNone

let destNullableTy g ty = 
    match tryDestNullableTy g ty with 
    | ValueSome ty -> ty
    | ValueNone -> failwith "destNullableTy: not a Nullable type"

[<return: Struct>]
let (|NullableTy|_|) g ty =
    match tryAppTy g ty with 
    | ValueSome (tcref, [tyarg]) when tyconRefEq g tcref g.system_Nullable_tcref -> ValueSome tyarg
    | _ -> ValueNone

let (|StripNullableTy|) g ty = 
    match tryDestNullableTy g ty with 
    | ValueSome tyarg -> tyarg
    | _ -> ty

let isLinqExpressionTy g ty = 
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> tyconRefEq g g.system_LinqExpression_tcref tcref

let tryDestLinqExpressionTy g ty = 
    match argsOfAppTy g ty with 
    | [ty1] when isLinqExpressionTy g ty -> Some ty1
    | _ -> None

let destLinqExpressionTy g ty = 
    match tryDestLinqExpressionTy g ty with 
    | Some ty -> ty
    | None -> failwith "destLinqExpressionTy: not an expression type"

let mkNoneCase (g: TcGlobals) = mkUnionCaseRef g.option_tcr_canon "None"

let mkSomeCase (g: TcGlobals) = mkUnionCaseRef g.option_tcr_canon "Some"

let mkSome g ty arg m = mkUnionCaseExpr(mkSomeCase g, [ty], [arg], m)

let mkNone g ty m = mkUnionCaseExpr(mkNoneCase g, [ty], [], m)

let mkValueNoneCase (g: TcGlobals) = mkUnionCaseRef g.valueoption_tcr_canon "ValueNone"

let mkValueSomeCase (g: TcGlobals) = mkUnionCaseRef g.valueoption_tcr_canon "ValueSome"

let mkAnySomeCase g isStruct = (if isStruct then mkValueSomeCase g else mkSomeCase g)

let mkValueSome g ty arg m = mkUnionCaseExpr(mkValueSomeCase g, [ty], [arg], m)

let mkValueNone g ty m = mkUnionCaseExpr(mkValueNoneCase g, [ty], [], m)

type ValRef with 
    member vref.IsDispatchSlot = 
        match vref.MemberInfo with 
        | Some membInfo -> membInfo.MemberFlags.IsDispatchSlot 
        | None -> false

[<return: Struct>]
let (|UnopExpr|_|) _g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, _, [arg1], _) -> ValueSome (vref, arg1)
    | _ -> ValueNone

[<return: Struct>]
let (|BinopExpr|_|) _g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, _, [arg1;arg2], _) -> ValueSome (vref, arg1, arg2)
    | _ -> ValueNone

[<return: Struct>]
let (|SpecificUnopExpr|_|) g vrefReqd expr = 
    match expr with 
    | UnopExpr g (vref, arg1) when valRefEq g vref vrefReqd -> ValueSome arg1
    | _ -> ValueNone

[<return: Struct>]
let (|SignedConstExpr|_|) expr =
    match expr with
    | Expr.Const (Const.Int32 _, _, _)
    | Expr.Const (Const.SByte _, _, _)
    | Expr.Const (Const.Int16 _, _, _)
    | Expr.Const (Const.Int64 _, _, _)
    | Expr.Const (Const.Single _, _, _)
    | Expr.Const (Const.Double _, _, _) -> ValueSome ()
    | _ -> ValueNone

[<return: Struct>]
let (|IntegerConstExpr|_|) expr =
    match expr with
    | Expr.Const (Const.Int32 _, _, _)
    | Expr.Const (Const.SByte _, _, _)
    | Expr.Const (Const.Int16 _, _, _)
    | Expr.Const (Const.Int64 _, _, _)
    | Expr.Const (Const.Byte _, _, _)
    | Expr.Const (Const.UInt16 _, _, _)
    | Expr.Const (Const.UInt32 _, _, _)
    | Expr.Const (Const.UInt64 _, _, _) -> ValueSome ()
    | _ -> ValueNone

[<return: Struct>]
let (|FloatConstExpr|_|) expr =
    match expr with
    | Expr.Const (Const.Single _, _, _)
    | Expr.Const (Const.Double _, _, _) -> ValueSome ()
    | _ -> ValueNone

[<return: Struct>]
let (|SpecificBinopExpr|_|) g vrefReqd expr = 
    match expr with 
    | BinopExpr g (vref, arg1, arg2) when valRefEq g vref vrefReqd -> ValueSome (arg1, arg2)
    | _ -> ValueNone

[<return: Struct>]
let (|EnumExpr|_|) g expr = 
    match (|SpecificUnopExpr|_|) g g.enum_vref expr with
    | ValueNone -> (|SpecificUnopExpr|_|) g g.enumOfValue_vref expr
    | x -> x

[<return: Struct>]
let (|BitwiseOrExpr|_|) g expr = (|SpecificBinopExpr|_|) g g.bitwise_or_vref expr

[<return: Struct>]
let (|AttribBitwiseOrExpr|_|) g expr = 
    match expr with 
    | BitwiseOrExpr g (arg1, arg2) -> ValueSome(arg1, arg2)
    // Special workaround, only used when compiling FSharp.Core.dll. Uses of 'a ||| b' occur before the '|||' bitwise or operator
    // is defined. These get through type checking because enums implicitly support the '|||' operator through
    // the automatic resolution of undefined operators (see tc.fs, Item.ImplicitOp). This then compiles as an 
    // application of a lambda to two arguments. We recognize this pattern here
    | Expr.App (Expr.Lambda _, _, _, [arg1;arg2], _) when g.compilingFSharpCore -> 
        ValueSome(arg1, arg2)
    | _ -> ValueNone

let isUncheckedDefaultOfValRef g vref = 
    valRefEq g vref g.unchecked_defaultof_vref 
    // There is an internal version of typeof defined in prim-types.fs that needs to be detected
    || (g.compilingFSharpCore && vref.LogicalName = "defaultof") 

let isTypeOfValRef g vref = 
    valRefEq g vref g.typeof_vref 
    // There is an internal version of typeof defined in prim-types.fs that needs to be detected
    || (g.compilingFSharpCore && vref.LogicalName = "typeof") 

let isSizeOfValRef g vref = 
    valRefEq g vref g.sizeof_vref 
    // There is an internal version of typeof defined in prim-types.fs that needs to be detected
    || (g.compilingFSharpCore && vref.LogicalName = "sizeof") 

let isNameOfValRef g vref =
    valRefEq g vref g.nameof_vref
    // There is an internal version of nameof defined in prim-types.fs that needs to be detected
    || (g.compilingFSharpCore && vref.LogicalName = "nameof")

let isTypeDefOfValRef g vref = 
    valRefEq g vref g.typedefof_vref 
    // There is an internal version of typedefof defined in prim-types.fs that needs to be detected
    || (g.compilingFSharpCore && vref.LogicalName = "typedefof") 

[<return: Struct>]
let (|UncheckedDefaultOfExpr|_|) g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, [ty], [], _) when isUncheckedDefaultOfValRef g vref -> ValueSome ty
    | _ -> ValueNone

[<return: Struct>]
let (|TypeOfExpr|_|) g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, [ty], [], _) when isTypeOfValRef g vref -> ValueSome ty
    | _ -> ValueNone

[<return: Struct>]
let (|SizeOfExpr|_|) g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, [ty], [], _) when isSizeOfValRef g vref -> ValueSome ty
    | _ -> ValueNone

[<return: Struct>]
let (|TypeDefOfExpr|_|) g expr = 
    match expr with 
    | Expr.App (Expr.Val (vref, _, _), _, [ty], [], _) when isTypeDefOfValRef g vref -> ValueSome ty
    | _ -> ValueNone

[<return: Struct>]
let (|NameOfExpr|_|) g expr = 
    match expr with 
    | Expr.App(Expr.Val(vref,_,_),_,[ty],[],_) when isNameOfValRef g vref  -> ValueSome ty
    | _ -> ValueNone

[<return: Struct>]
let (|SeqExpr|_|) g expr = 
    match expr with 
    | Expr.App(Expr.Val(vref,_,_),_,_,_,_) when valRefEq g vref g.seq_vref -> ValueSome()
    | _ -> ValueNone

//--------------------------------------------------------------------------
// DEBUG layout
//---------------------------------------------------------------------------
module DebugPrint = 
    let mutable layoutRanges = false
    let mutable layoutTypes = false
    let mutable layoutStamps = false
    let mutable layoutValReprInfo = false

    let braceBarL l = leftL leftBraceBar ^^ l ^^ rightL rightBraceBar

    let intL (n: int) = wordL (tagNumericLiteral (string n))

    let qlistL f xmap = QueueList.foldBack (fun x z -> z @@ f x) xmap emptyL

    let bracketIfL b lyt = if b then bracketL lyt else lyt

    let lvalopL x = 
        match x with 
        | LAddrOf false -> wordL (tagText "&")
        | LAddrOf true -> wordL (tagText "&!")
        | LByrefGet -> wordL (tagText "*")
        | LSet -> wordL (tagText "LSet")
        | LByrefSet -> wordL (tagText "LByrefSet")

    let angleBracketL l = leftL (tagText "<") ^^ l ^^ rightL (tagText ">")

    let angleBracketListL l = angleBracketL (sepListL (sepL (tagText ",")) l)

#if DEBUG
    let layoutMemberFlags (memFlags: SynMemberFlags) = 
        let stat = 
            if memFlags.IsInstance || (memFlags.MemberKind = SynMemberKind.Constructor) then emptyL 
            else wordL (tagText "static")
        let stat =
            if memFlags.IsDispatchSlot then stat ++ wordL (tagText "abstract")
            elif memFlags.IsOverrideOrExplicitImpl then stat ++ wordL (tagText "override")
            else stat
        stat
#endif

    let stampL (n: Stamp) w = 
        if layoutStamps then w ^^ wordL (tagText ("#" + string n)) else w

    let layoutTyconRef (tcref: TyconRef) = 
        wordL (tagText tcref.DisplayNameWithStaticParameters) |> stampL tcref.Stamp

    let rec auxTypeL env ty = auxTypeWrapL env false ty

    and auxTypeAtomL env ty = auxTypeWrapL env true ty

    and auxTyparsL env tcL prefix tinst = 
       match tinst with 
       | [] -> tcL
       | [t] -> 
         let tL = auxTypeAtomL env t
         if prefix then tcL ^^ angleBracketL tL 
         else tL ^^ tcL 
       | _ -> 
         let tinstL = List.map (auxTypeL env) tinst
         if prefix then
             tcL ^^ angleBracketListL tinstL
         else
             tupleL tinstL ^^ tcL
            
    and auxAddNullness coreL (nullness: Nullness) = 
        match nullness.Evaluate() with
        | NullnessInfo.WithNull -> coreL ^^ wordL (tagText "?")
        | NullnessInfo.WithoutNull -> coreL
        | NullnessInfo.AmbivalentToNull -> coreL //^^ wordL (tagText "%")

    and auxTypeWrapL env isAtomic ty = 
        let wrap x = bracketIfL isAtomic x in // wrap iff require atomic expr 
        match stripTyparEqns ty with
        | TType_forall (typars, bodyTy) -> 
           (leftL (tagText "!") ^^ layoutTyparDecls typars --- auxTypeL env bodyTy) |> wrap

        | TType_ucase (UnionCaseRef(tcref, _), tinst) ->
           let prefix = tcref.IsPrefixDisplay
           let tcL = layoutTyconRef tcref
           auxTyparsL env tcL prefix tinst

        | TType_app (tcref, tinst, nullness) ->
           let prefix = tcref.IsPrefixDisplay
           let tcL = layoutTyconRef tcref
           let coreL = auxTyparsL env tcL prefix tinst
           auxAddNullness coreL nullness

        | TType_tuple (_tupInfo, tys) ->
            sepListL (wordL (tagText "*")) (List.map (auxTypeAtomL env) tys) |> wrap

        | TType_fun (domainTy, rangeTy, nullness) ->
           let coreL = ((auxTypeAtomL env domainTy ^^ wordL (tagText "->")) --- auxTypeL env rangeTy)  |> wrap
           auxAddNullness coreL nullness

        | TType_var (typar, nullness) ->
           let coreL = auxTyparWrapL env isAtomic typar
           auxAddNullness coreL nullness

        | TType_anon (anonInfo, tys) ->
           braceBarL (sepListL (wordL (tagText ";")) (List.map2 (fun nm ty -> wordL (tagField nm) --- auxTypeAtomL env ty) (Array.toList anonInfo.SortedNames) tys))

        | TType_measure unt ->
#if DEBUG
          leftL (tagText "{") ^^
          (match global_g with
           | None -> wordL (tagText "<no global g>")
           | Some g -> 
             let sortVars (vs:(Typar * Rational) list) = vs |> List.sortBy (fun (v, _) -> v.DisplayName) 
             let sortCons (cs:(TyconRef * Rational) list) = cs |> List.sortBy (fun (c, _) -> c.DisplayName) 
             let negvs, posvs = ListMeasureVarOccsWithNonZeroExponents unt |> sortVars |> List.partition (fun (_, e) -> SignRational e < 0)
             let negcs, poscs = ListMeasureConOccsWithNonZeroExponents g false unt |> sortCons |> List.partition (fun (_, e) -> SignRational e < 0)
             let unparL (uv: Typar) = wordL (tagText ("'" + uv.DisplayName))
             let unconL tcref = layoutTyconRef tcref
             let rationalL e = wordL (tagText(RationalToString e))
             let measureToPowerL x e = if e = OneRational then x else x -- wordL (tagText "^") -- rationalL e
             let prefix =
                 spaceListL
                     (List.map (fun (v, e) -> measureToPowerL (unparL v) e) posvs @
                      List.map (fun (c, e) -> measureToPowerL (unconL c) e) poscs)
             let postfix =
                 spaceListL 
                     (List.map (fun (v, e) -> measureToPowerL (unparL v) (NegRational e)) negvs @
                      List.map (fun (c, e) -> measureToPowerL (unconL c) (NegRational e)) negcs)
             match (negvs, negcs) with 
             | [], [] -> prefix 
             | _ -> prefix ^^ sepL (tagText "/") ^^ postfix) ^^
          rightL (tagText "}")
#else
          unt |> ignore
          wordL(tagText "<measure>")
#endif

    and auxTyparWrapL (env: SimplifyTypes.TypeSimplificationInfo) isAtomic (typar: Typar) =

          let tpText =
              prefixOfStaticReq typar.StaticReq
              + prefixOfInferenceTypar typar
              + typar.DisplayName

          let tpL = wordL (tagText tpText)

          let varL = tpL |> stampL typar.Stamp 

          // There are several cases for pprinting of typar.
          // 
          //   'a - is multiple occurrence.
          //   #Type - inplace coercion constraint and singleton
          //   ('a :> Type) - inplace coercion constraint not singleton
          //   ('a.opM: S->T) - inplace operator constraint
          match Zmap.tryFind typar env.inplaceConstraints with
          | Some typarConstraintTy ->
              if Zset.contains typar env.singletons then
                leftL (tagText "#") ^^ auxTyparConstraintTypL env typarConstraintTy
              else
                (varL ^^ sepL (tagText ":>") ^^ auxTyparConstraintTypL env typarConstraintTy) |> bracketIfL isAtomic
          | _ -> varL

    and auxTypar2L env typar = auxTyparWrapL env false typar

    and auxTyparConstraintTypL env ty = auxTypeL env ty

    and auxTraitL env (ttrait: TraitConstraintInfo) =
#if DEBUG
        let (TTrait(tys, nm, memFlags, argTys, retTy, _, _)) = ttrait 
        match global_g with
        | None -> wordL (tagText "<no global g>")
        | Some g -> 
            let retTy = GetFSharpViewOfReturnType g retTy
            let stat = layoutMemberFlags memFlags
            let argsL = sepListL (wordL (tagText "*")) (List.map (auxTypeAtomL env) argTys)
            let resL = auxTypeL env retTy
            let methodTypeL = (argsL ^^ wordL (tagText "->")) ++ resL
            bracketL (stat ++ bracketL (sepListL (wordL (tagText "or")) (List.map (auxTypeAtomL env) tys)) ++ wordL (tagText "member") --- (wordL (tagText nm) ^^ wordL (tagText ":") -- methodTypeL))
#else
        ignore (env, ttrait)
        wordL(tagText "trait")
#endif

    and auxTyparConstraintL env (tp, tpc) = 
        let constraintPrefix l = auxTypar2L env tp ^^ wordL (tagText ":") ^^ l
        match tpc with
        | TyparConstraint.CoercesTo(typarConstraintTy, _) ->
            auxTypar2L env tp ^^ wordL (tagText ":>") --- auxTyparConstraintTypL env typarConstraintTy
        | TyparConstraint.MayResolveMember(traitInfo, _) ->
            auxTypar2L env tp ^^ wordL (tagText ":") --- auxTraitL env traitInfo
        | TyparConstraint.DefaultsTo(_, ty, _) ->
            wordL (tagText "default") ^^ auxTypar2L env tp ^^ wordL (tagText ":") ^^ auxTypeL env ty
        | TyparConstraint.IsEnum(ty, _) ->
            auxTyparsL env (wordL (tagText "enum")) true [ty] |> constraintPrefix
        | TyparConstraint.IsDelegate(aty, bty, _) ->
            auxTyparsL env (wordL (tagText "delegate")) true [aty; bty] |> constraintPrefix
        | TyparConstraint.SupportsNull _ ->
            wordL (tagText "null") |> constraintPrefix
        | TyparConstraint.SupportsComparison _ ->
            wordL (tagText "comparison") |> constraintPrefix
        | TyparConstraint.SupportsEquality _ ->
            wordL (tagText "equality") |> constraintPrefix
        | TyparConstraint.IsNonNullableStruct _ ->
            wordL (tagText "struct") |> constraintPrefix
        | TyparConstraint.IsReferenceType _ ->
            wordL (tagText "not struct") |> constraintPrefix
        | TyparConstraint.NotSupportsNull _ ->
            wordL (tagText "not null") |> constraintPrefix
        | TyparConstraint.IsUnmanaged _ ->
            wordL (tagText "unmanaged") |> constraintPrefix
        | TyparConstraint.AllowsRefStruct _ ->
            wordL (tagText "allows ref struct") |> constraintPrefix
        | TyparConstraint.SimpleChoice(tys, _) ->
            bracketL (sepListL (sepL (tagText "|")) (List.map (auxTypeL env) tys)) |> constraintPrefix
        | TyparConstraint.RequiresDefaultConstructor _ ->
            bracketL (wordL (tagText "new : unit -> ") ^^ (auxTypar2L env tp)) |> constraintPrefix

    and auxTyparConstraintsL env x = 
        match x with 
        | [] -> emptyL
        | cxs -> wordL (tagText "when") --- aboveListL (List.map (auxTyparConstraintL env) cxs)

    and typarL tp = auxTypar2L SimplifyTypes.typeSimplificationInfo0 tp 

    and typeAtomL tau =
        let tau, cxs = tau, []
        let env = SimplifyTypes.CollectInfo false [tau] cxs
        match env.postfixConstraints with
        | [] -> auxTypeAtomL env tau
        | _ -> bracketL (auxTypeL env tau --- auxTyparConstraintsL env env.postfixConstraints)
          
    and typeL tau =
        let tau, cxs = tau, []
        let env = SimplifyTypes.CollectInfo false [tau] cxs
        match env.postfixConstraints with
        | [] -> auxTypeL env tau 
        | _ -> (auxTypeL env tau --- auxTyparConstraintsL env env.postfixConstraints) 

    and typarDeclL tp =
        let tau, cxs = mkTyparTy tp, (List.map (fun x -> (tp, x)) tp.Constraints)
        let env = SimplifyTypes.CollectInfo false [tau] cxs
        match env.postfixConstraints with
        | [] -> auxTypeL env tau 
        | _ -> (auxTypeL env tau --- auxTyparConstraintsL env env.postfixConstraints) 
    and layoutTyparDecls tps =
        match tps with
        | [] -> emptyL
        | _ -> angleBracketListL (List.map typarDeclL tps) 

    let rangeL m = wordL (tagText (stringOfRange m))

    let instL tyL tys =
        if layoutTypes then
            match tys with
            | [] -> emptyL
            | tys -> sepL (tagText "@[") ^^ commaListL (List.map tyL tys) ^^ rightL (tagText "]")
        else
            emptyL

    let valRefL (vr: ValRef) = 
        wordL (tagText vr.LogicalName) |> stampL vr.Stamp 

    let layoutAttrib (Attrib(_, k, _, _, _, _, _)) = 
        leftL (tagText "[<") ^^ 
        (match k with 
         | ILAttrib ilmeth -> wordL (tagText ilmeth.Name)
         | FSAttrib vref -> valRefL vref) ^^
        rightL (tagText ">]")

    let layoutAttribs attribs = aboveListL (List.map layoutAttrib attribs)

    let valReprInfoL (ValReprInfo (tpNames, _, _) as tvd) = 
        let ns = tvd.AritiesOfArgs
        leftL (tagText "<") ^^ intL tpNames.Length ^^ sepL (tagText ">[") ^^ commaListL (List.map intL ns) ^^ rightL (tagText "]")

    let valL (v: Val) =
        let vsL = wordL (tagText (ConvertValLogicalNameToDisplayNameCore v.LogicalName)) |> stampL v.Stamp
        let vsL = vsL -- layoutAttribs v.Attribs
        vsL

    let typeOfValL (v: Val) =
        valL v
          ^^ (if v.ShouldInline then wordL (tagText "inline ") else emptyL) 
          ^^ (if v.IsMutable then wordL(tagText "mutable ") else emptyL)
          ^^ (if layoutTypes then wordL (tagText ":") ^^ typeL v.Type else emptyL)

#if DEBUG
    let tslotparamL (TSlotParam(nmOpt, ty, inFlag, outFlag, _, _)) =
        (optionL (tagText >> wordL) nmOpt) ^^ 
         wordL(tagText ":") ^^ 
         typeL ty ^^ 
         (if inFlag then wordL(tagText "[in]") else emptyL) ^^ 
         (if outFlag then wordL(tagText "[out]") else emptyL) ^^ 
         (if inFlag then wordL(tagText "[opt]") else emptyL)
#endif

    let slotSigL (slotsig: SlotSig) =
#if DEBUG
        let (TSlotSig(nm, ty, tps1, tps2, pms, retTy)) = slotsig 
        match global_g with
        | None -> wordL(tagText "<no global g>")
        | Some g -> 
            let retTy = GetFSharpViewOfReturnType g retTy
            (wordL(tagText "slot") --- (wordL (tagText nm)) ^^ wordL(tagText "@") ^^ typeL ty) --
              (wordL(tagText "LAM") --- spaceListL (List.map typarL tps1) ^^ rightL(tagText ".")) ---
              (wordL(tagText "LAM") --- spaceListL (List.map typarL tps2) ^^ rightL(tagText ".")) ---
              (commaListL (List.map (List.map tslotparamL >> tupleL) pms)) ^^ (wordL(tagText "-> ")) --- (typeL retTy) 
#else
        ignore slotsig
        wordL(tagText "slotsig")
#endif

    let valAtBindL v =
        let vL = valL v
        let vL = (if v.IsMutable then wordL(tagText "mutable") ++ vL else vL)
        let vL =
            if layoutTypes then
                vL ^^ wordL(tagText ":") ^^ typeL v.Type
            else
                vL
        let vL =
            match v.ValReprInfo with
            | Some info when layoutValReprInfo -> vL ^^ wordL(tagText "!") ^^ valReprInfoL info
            | _ -> vL
        vL

    let unionCaseRefL (ucr: UnionCaseRef) = wordL (tagText ucr.CaseName)

    let recdFieldRefL (rfref: RecdFieldRef) = wordL (tagText rfref.FieldName)

    // Note: We need nice printing of constants in order to print literals and attributes 
    let constL c =
        let str = 
            match c with
            | Const.Bool x -> if x then "true" else "false"
            | Const.SByte x -> (x |> string)+"y"
            | Const.Byte x -> (x |> string)+"uy"
            | Const.Int16 x -> (x |> string)+"s"
            | Const.UInt16 x -> (x |> string)+"us"
            | Const.Int32 x -> (x |> string)
            | Const.UInt32 x -> (x |> string)+"u"
            | Const.Int64 x -> (x |> string)+"L"
            | Const.UInt64 x -> (x |> string)+"UL"
            | Const.IntPtr x -> (x |> string)+"n"
            | Const.UIntPtr x -> (x |> string)+"un"
            | Const.Single d -> 
                (let s = d.ToString("g12", System.Globalization.CultureInfo.InvariantCulture)
                 if String.forall (fun c -> System.Char.IsDigit c || c = '-') s 
                 then s + ".0" 
                 else s) + "f"
            | Const.Double d -> 
                let s = d.ToString("g12", System.Globalization.CultureInfo.InvariantCulture)
                if String.forall (fun c -> System.Char.IsDigit c || c = '-') s 
                then s + ".0" 
                else s
            | Const.Char c -> "'" + c.ToString() + "'" 
            | Const.String bs -> "\"" + bs + "\"" 
            | Const.Unit -> "()" 
            | Const.Decimal bs -> string bs + "M" 
            | Const.Zero -> "default"
        wordL (tagText str)


    let layoutUnionCaseArgTypes argTys = sepListL (wordL(tagText "*")) (List.map typeL argTys)

    let ucaseL prefixL (ucase: UnionCase) =
        let nmL = wordL (tagText ucase.DisplayName)
        match ucase.RecdFields |> List.map (fun rfld -> rfld.FormalType) with
        | [] -> (prefixL ^^ nmL)
        | argTys -> (prefixL ^^ nmL ^^ wordL(tagText "of")) --- layoutUnionCaseArgTypes argTys

    let layoutUnionCases ucases =
        let prefixL = if not (isNilOrSingleton ucases) then wordL(tagText "|") else emptyL
        List.map (ucaseL prefixL) ucases
            
    let layoutRecdField (fld: RecdField) =
        let lhs = wordL (tagText fld.LogicalName)
        let lhs = if fld.IsMutable then wordL(tagText "mutable") --- lhs else lhs
        let lhs = if layoutTypes then lhs ^^ rightL(tagText ":") ^^ typeL fld.FormalType else lhs
        lhs

    let tyconReprL (repr, tycon: Tycon) = 
        match repr with 
        | TFSharpTyconRepr { fsobjmodel_kind = TFSharpUnion } -> 
            tycon.UnionCasesAsList |> layoutUnionCases |> aboveListL 
        | TFSharpTyconRepr r -> 
            match r.fsobjmodel_kind with 
            | TFSharpDelegate _ ->
                wordL(tagText "delegate ...")
            | _ ->
                let start = 
                    match r.fsobjmodel_kind with
                    | TFSharpClass -> "class" 
                    | TFSharpInterface -> "interface" 
                    | TFSharpStruct -> "struct" 
                    | TFSharpEnum -> "enum" 
                    | _ -> failwith "???"

                let inherits = 
                    match r.fsobjmodel_kind, tycon.TypeContents.tcaug_super with
                    | TFSharpClass, Some super -> [wordL(tagText "inherit") ^^ (typeL super)] 
                    | TFSharpInterface, _ -> 
                        tycon.ImmediateInterfacesOfFSharpTycon
                        |> List.filter (fun (_, compgen, _) -> not compgen)
                        |> List.map (fun (ity, _, _) -> wordL(tagText "inherit") ^^ (typeL ity))
                    | _ -> []

                let vsprs = 
                    tycon.MembersOfFSharpTyconSorted 
                        |> List.filter (fun v -> v.IsDispatchSlot) 
                        |> List.map (fun vref -> valAtBindL vref.Deref)

                let vals = tycon.TrueFieldsAsList |> List.map (fun f -> (if f.IsStatic then wordL(tagText "static") else emptyL) ^^ wordL(tagText "val") ^^ layoutRecdField f)

                let alldecls = inherits @ vsprs @ vals

                let emptyMeasure = match tycon.TypeOrMeasureKind with TyparKind.Measure -> isNil alldecls | _ -> false

                if emptyMeasure then emptyL else (wordL (tagText start) @@-- aboveListL alldecls) @@ wordL(tagText "end")

        | TAsmRepr _ -> wordL(tagText "(# ... #)")
        | TMeasureableRepr ty -> typeL ty
        | TILObjectRepr (TILObjectReprData(_, _, td)) -> wordL (tagText td.Name)
        | _ -> failwith "unreachable"

    let rec bindingL (TBind(v, repr, _)) =
        (valAtBindL v ^^ wordL(tagText "=")) @@-- exprL repr

    and exprL expr =
        exprWrapL false expr

    and atomL expr =
        // true means bracket if needed to be atomic expr 
        exprWrapL true expr

    and letRecL binds bodyL = 
        let eqnsL = 
            binds
               |> List.mapHeadTail (fun bind -> wordL(tagText "rec") ^^ bindingL bind ^^ wordL(tagText "in"))
                              (fun bind -> wordL(tagText "and") ^^ bindingL bind ^^ wordL(tagText "in")) 
        (aboveListL eqnsL @@ bodyL) 

    and letL bind bodyL = 
        let eqnL = wordL(tagText "let") ^^ bindingL bind
        (eqnL @@ bodyL) 

    and exprWrapL isAtomic expr =
        let wrap = bracketIfL isAtomic // wrap iff require atomic expr 
        let lay =
            match expr with
            | Expr.Const (c, _, _) -> constL c

            | Expr.Val (v, flags, _) -> 
                 let xL = valL v.Deref 
                 let xL =
                     match flags with
                     | PossibleConstrainedCall _ -> xL ^^ rightL(tagText "<constrained>")
                     | CtorValUsedAsSelfInit -> xL ^^ rightL(tagText "<selfinit>")
                     | CtorValUsedAsSuperInit -> xL ^^ rightL(tagText "<superinit>")
                     | VSlotDirectCall -> xL ^^ rightL(tagText "<vdirect>")
                     | NormalValUse -> xL 
                 xL

            | Expr.Sequential (expr1, expr2, flag, _) -> 
                aboveListL [
                    exprL expr1
                    match flag with
                    | NormalSeq -> ()
                    | ThenDoSeq -> wordL (tagText "ThenDo")
                    exprL expr2 
                ]
                |> wrap

            | Expr.Lambda (_, _, baseValOpt, argvs, body, _, _) -> 
                let formalsL = spaceListL (List.map valAtBindL argvs)
                let bindingL = 
                    match baseValOpt with
                    | None -> wordL(tagText "fun") ^^ formalsL ^^ wordL(tagText "->")
                    | Some basev -> wordL(tagText "fun") ^^ (leftL(tagText "base=") ^^ valAtBindL basev) --- formalsL ^^ wordL(tagText "->")
                (bindingL @@-- exprL body) |> wrap

            | Expr.TyLambda (_, tps, body, _, _) -> 
                ((wordL(tagText "FUN") ^^ layoutTyparDecls tps ^^ wordL(tagText "->")) ++ exprL body) |> wrap

            | Expr.TyChoose (tps, body, _) -> 
                ((wordL(tagText "CHOOSE") ^^ layoutTyparDecls tps ^^ wordL(tagText "->")) ++ exprL body) |> wrap

            | Expr.App (f, _, tys, argTys, _) -> 
                let flayout = atomL f
                appL flayout tys argTys |> wrap

            | Expr.LetRec (binds, body, _, _) -> 
                letRecL binds (exprL body) |> wrap

            | Expr.Let (bind, body, _, _) -> 
                letL bind (exprL body) |> wrap

            | Expr.Link rX -> 
                exprL rX.Value |> wrap

            | Expr.DebugPoint (DebugPointAtLeafExpr.Yes m, rX) -> 
                aboveListL [ wordL(tagText "__debugPoint(") ^^ rangeL m ^^ wordL (tagText ")"); exprL rX ] |> wrap

            | Expr.Match (_, _, dtree, targets, _, _) -> 
                leftL(tagText "[") ^^ (decisionTreeL dtree @@ aboveListL (List.mapi targetL (targets |> Array.toList)) ^^ rightL(tagText "]"))

            | Expr.Op (TOp.UnionCase c, _, args, _) -> 
                (unionCaseRefL c ++ spaceListL (List.map atomL args)) |> wrap

            | Expr.Op (TOp.ExnConstr ecref, _, args, _) -> 
                wordL (tagText ecref.LogicalName) ^^ bracketL (commaListL (List.map atomL args))

            | Expr.Op (TOp.Tuple _, _, xs, _) -> 
                tupleL (List.map exprL xs)

            | Expr.Op (TOp.Recd (ctor, tcref), _, xs, _) -> 
                let fields = tcref.TrueInstanceFieldsAsList
                let lay fs x = (wordL (tagText fs.rfield_id.idText) ^^ sepL(tagText "=")) --- (exprL x)
                let ctorL = 
                    match ctor with
                    | RecdExpr -> emptyL
                    | RecdExprIsObjInit-> wordL(tagText "(new)")
                leftL(tagText "{") ^^ aboveListL (List.map2 lay fields xs) ^^ rightL(tagText "}") ^^ ctorL

            | Expr.Op (TOp.ValFieldSet rf, _, [rx;x], _) -> 
                (atomL rx --- wordL(tagText ".")) ^^ (recdFieldRefL rf ^^ wordL(tagText "<-") --- exprL x)

            | Expr.Op (TOp.ValFieldSet rf, _, [x], _) -> 
                recdFieldRefL rf ^^ wordL(tagText "<-") --- exprL x

            | Expr.Op (TOp.ValFieldGet rf, _, [rx], _) -> 
                atomL rx ^^ rightL(tagText ".#") ^^ recdFieldRefL rf

            | Expr.Op (TOp.ValFieldGet rf, _, [], _) -> 
                recdFieldRefL rf

            | Expr.Op (TOp.ValFieldGetAddr (rf, _), _, [rx], _) -> 
                leftL(tagText "&") ^^ bracketL (atomL rx ^^ rightL(tagText ".!") ^^ recdFieldRefL rf)

            | Expr.Op (TOp.ValFieldGetAddr (rf, _), _, [], _) -> 
                leftL(tagText "&") ^^ (recdFieldRefL rf)

            | Expr.Op (TOp.UnionCaseTagGet tycr, _, [x], _) -> 
                wordL (tagText (tycr.LogicalName + ".tag")) ^^ atomL x

            | Expr.Op (TOp.UnionCaseProof c, _, [x], _) -> 
                wordL (tagText (c.CaseName + ".proof")) ^^ atomL x

            | Expr.Op (TOp.UnionCaseFieldGet (c, i), _, [x], _) -> 
                wordL (tagText (c.CaseName + "." + string i)) --- atomL x

            | Expr.Op (TOp.UnionCaseFieldSet (c, i), _, [x;y], _) -> 
                ((atomL x --- (rightL (tagText ("#" + c.CaseName + "." + string i)))) ^^ wordL(tagText ":=")) --- exprL y

            | Expr.Op (TOp.TupleFieldGet (_, i), _, [x], _) -> 
                wordL (tagText ("#" + string i)) --- atomL x

            | Expr.Op (TOp.Coerce, [ty;_], [x], _) -> 
                atomL x --- (wordL(tagText ":>") ^^ typeL ty) 

            | Expr.Op (TOp.Reraise, [_], [], _) -> 
                wordL(tagText "Reraise")

            | Expr.Op (TOp.ILAsm (instrs, retTypes), tyargs, args, _) -> 
                let instrs = instrs |> List.map (sprintf "%+A" >> tagText >> wordL) |> spaceListL // %+A has + since instrs are from an "internal" type  
                let instrs = leftL(tagText "(#") ^^ instrs ^^ rightL(tagText "#)")
                let instrL = appL instrs tyargs args
                let instrL = if layoutTypes then instrL ^^ wordL(tagText ":") ^^ spaceListL (List.map typeAtomL retTypes) else instrL
                instrL |> wrap

            | Expr.Op (TOp.LValueOp (lvop, vr), _, args, _) -> 
                (lvalopL lvop ^^ valRefL vr --- bracketL (commaListL (List.map atomL args))) |> wrap

            | Expr.Op (TOp.ILCall (_, _, _, _, _, _, _, ilMethRef, _enclTypeInst, _methInst, _), _tyargs, args, _) ->
                let meth = ilMethRef.Name
                (wordL (tagText ilMethRef.DeclaringTypeRef.FullName) ^^ sepL(tagText ".") ^^ wordL (tagText meth)) ---- 
                    (if args.IsEmpty then wordL (tagText "()") else listL exprL args) 
                        //if not enclTypeInst.IsEmpty then yield wordL(tagText "tinst ") --- listL typeL enclTypeInst
                        //if not methInst.IsEmpty then yield wordL (tagText "minst ") --- listL typeL methInst
                        //if not tyargs.IsEmpty then yield wordL (tagText "tyargs") --- listL typeL tyargs
                        
                |> wrap

            | Expr.Op (TOp.Array, [_], xs, _) -> 
                leftL(tagText "[|") ^^ commaListL (List.map exprL xs) ^^ rightL(tagText "|]")

            | Expr.Op (TOp.While _, [], [Expr.Lambda (_, _, _, [_], x1, _, _);Expr.Lambda (_, _, _, [_], x2, _, _)], _) -> 
                let headerL = wordL(tagText "while") ^^ exprL x1 ^^ wordL(tagText "do")
                headerL @@-- exprL x2

            | Expr.Op (TOp.IntegerForLoop _, [], [Expr.Lambda (_, _, _, [_], x1, _, _);Expr.Lambda (_, _, _, [_], x2, _, _);Expr.Lambda (_, _, _, [_], x3, _, _)], _) -> 
                let headerL = wordL(tagText "for") ^^ exprL x1 ^^ wordL(tagText "to") ^^ exprL x2 ^^ wordL(tagText "do")
                headerL @@-- exprL x3

            | Expr.Op (TOp.TryWith _, [_], [Expr.Lambda (_, _, _, [_], x1, _, _);Expr.Lambda (_, _, _, [_], xf, _, _);Expr.Lambda (_, _, _, [_], xh, _, _)], _) ->
                (wordL (tagText "try") @@-- exprL x1) @@ (wordL(tagText "with-filter") @@-- exprL xf) @@ (wordL(tagText "with") @@-- exprL xh)

            | Expr.Op (TOp.TryFinally _, [_], [Expr.Lambda (_, _, _, [_], x1, _, _);Expr.Lambda (_, _, _, [_], x2, _, _)], _) -> 
                (wordL (tagText "try") @@-- exprL x1) @@ (wordL(tagText "finally") @@-- exprL x2)
            | Expr.Op (TOp.Bytes _, _, _, _) -> 
                wordL(tagText "bytes++")

            | Expr.Op (TOp.UInt16s _, _, _, _) -> wordL(tagText "uint16++")
            | Expr.Op (TOp.RefAddrGet _, _tyargs, _args, _) -> wordL(tagText "GetRefLVal...")
            | Expr.Op (TOp.TraitCall _, _tyargs, _args, _) -> wordL(tagText "traitcall...")
            | Expr.Op (TOp.ExnFieldGet _, _tyargs, _args, _) -> wordL(tagText "TOp.ExnFieldGet...")
            | Expr.Op (TOp.ExnFieldSet _, _tyargs, _args, _) -> wordL(tagText "TOp.ExnFieldSet...")
            | Expr.Op (TOp.TryFinally _, _tyargs, args, _) -> wordL(tagText ("unexpected-try-finally")) ---- aboveListL (List.map atomL args)
            | Expr.Op (TOp.TryWith _, _tyargs, args, _) -> wordL(tagText ("unexpected-try-with")) ---- aboveListL (List.map atomL args)
            | Expr.Op (TOp.Goto l, _tys, args, _) -> wordL(tagText ("Expr.Goto " + string l)) ^^ bracketL (commaListL (List.map atomL args)) 
            | Expr.Op (TOp.Label l, _tys, args, _) -> wordL(tagText ("Expr.Label " + string l)) ^^ bracketL (commaListL (List.map atomL args)) 
            | Expr.Op (_, _tys, args, _) -> wordL(tagText "Expr.Op ...") ^^ bracketL (commaListL (List.map atomL args)) 
            | Expr.Quote (a, _, _, _, _) -> leftL(tagText "<@") ^^ atomL a ^^ rightL(tagText "@>")

            | Expr.Obj (_lambdaId, ty, basev, ccall, overrides, iimpls, _) -> 
                (leftL (tagText "{") 
                 @@--
                  ((wordL(tagText "new ") ++ typeL ty) 
                   @@-- 
                   aboveListL [exprL ccall
                               match basev with 
                               | None -> ()
                               | Some b -> valAtBindL b
                               yield! List.map tmethodL overrides
                               yield! List.map iimplL iimpls]))
                @@
                rightL (tagText "}")

            | Expr.WitnessArg _ -> wordL (tagText "<witnessarg>")

            | Expr.StaticOptimization (_tcs, csx, x, _) -> 
                (wordL(tagText "opt") @@- (exprL x)) @@--
                   (wordL(tagText "|") ^^ exprL csx --- (wordL(tagText "when...") ))
           
        // For tracking ranges through expr rewrites 
        if layoutRanges then
            aboveListL [
                leftL(tagText "//") ^^ rangeL expr.Range
                lay
            ]
        else
            lay

    and appL flayout tys args =
        let z = flayout
        let z = if isNil tys then z else z ^^ instL typeL tys
        let z = if isNil args then z else z --- spaceListL (List.map atomL args)
        z

    and decisionTreeL x =
        match x with 
        | TDBind (bind, body) -> 
            let bind = wordL(tagText "let") ^^ bindingL bind
            (bind @@ decisionTreeL body) 
        | TDSuccess (args, n) -> 
            wordL(tagText "Success") ^^ leftL(tagText "T") ^^ intL n ^^ tupleL (args |> List.map (exprL))
        | TDSwitch (test, dcases, dflt, _) ->
            (wordL(tagText "Switch") --- exprL test) @@--
            (aboveListL (List.map dcaseL dcases) @@
             match dflt with
             | None -> emptyL
             | Some dtree -> wordL(tagText "dflt:") --- decisionTreeL dtree)

    and dcaseL (TCase (test, dtree)) = 
        (dtestL test ^^ wordL(tagText "//")) --- decisionTreeL dtree

    and dtestL x = 
        match x with 
        | DecisionTreeTest.UnionCase (c, tinst) -> wordL(tagText "is") ^^ unionCaseRefL c ^^ instL typeL tinst
        | DecisionTreeTest.ArrayLength (n, ty) -> wordL(tagText "length") ^^ intL n ^^ typeL ty
        | DecisionTreeTest.Const c -> wordL(tagText "is") ^^ constL c
        | DecisionTreeTest.IsNull -> wordL(tagText "isnull")
        | DecisionTreeTest.IsInst (_, ty) -> wordL(tagText "isinst") ^^ typeL ty
        | DecisionTreeTest.ActivePatternCase (exp, _, _, _, _, _) -> wordL(tagText "query") ^^ exprL exp
        | DecisionTreeTest.Error _ -> wordL (tagText "error recovery")
 
    and targetL i (TTarget (argvs, body, _)) =
        leftL(tagText "T") ^^ intL i ^^ tupleL (flatValsL argvs) ^^ rightL(tagText ":") --- exprL body

    and flatValsL vs = vs |> List.map valL

    and tmethodL (TObjExprMethod(TSlotSig(nm, _, _, _, _, _), _, tps, vs, e, _)) =
        (wordL(tagText "member") ^^ (wordL (tagText nm)) ^^ layoutTyparDecls tps ^^ tupleL (List.map (List.map valAtBindL >> tupleL) vs) ^^ rightL(tagText "="))
        @@--
          exprL e

    and iimplL (ty, tmeths) = wordL(tagText "impl") ^^ aboveListL (typeL ty :: List.map tmethodL tmeths) 

    let rec tyconL (tycon: Tycon) =

        let lhsL = wordL (tagText (match tycon.TypeOrMeasureKind with TyparKind.Measure -> "[<Measure>] type" | TyparKind.Type -> "type")) ^^ wordL (tagText tycon.DisplayName) ^^ layoutTyparDecls tycon.TyparsNoRange
        let lhsL = lhsL --- layoutAttribs tycon.Attribs
        let memberLs = 
            let adhoc = 
                tycon.MembersOfFSharpTyconSorted 
                    |> List.filter (fun v -> not v.IsDispatchSlot)
                    |> List.filter (fun v -> not v.Deref.IsClassConstructor) 
                    // Don't print individual methods forming interface implementations - these are currently never exported 
                    |> List.filter (fun v -> isNil (Option.get v.MemberInfo).ImplementedSlotSigs)
            let iimpls = 
                match tycon.TypeReprInfo with 
                | TFSharpTyconRepr r when (match r.fsobjmodel_kind with TFSharpInterface -> true | _ -> false) -> []
                | _ -> tycon.ImmediateInterfacesOfFSharpTycon
            let iimpls = iimpls |> List.filter (fun (_, compgen, _) -> not compgen)
            // if TFSharpInterface, the iimpls should be printed as inherited interfaces 
            if isNil adhoc && isNil iimpls then 
                emptyL 
            else 
                let iimplsLs = iimpls |> List.map (fun (ty, _, _) -> wordL(tagText "interface") --- typeL ty)
                let adhocLs = adhoc |> List.map (fun vref -> valAtBindL vref.Deref)
                (wordL(tagText "with") @@-- aboveListL (iimplsLs @ adhocLs)) @@ wordL(tagText "end")
        let reprL = 
            match tycon.TypeReprInfo with 
#if !NO_TYPEPROVIDERS
            | TProvidedTypeRepr _
            | TProvidedNamespaceRepr _
#endif
            | TNoRepr -> 
                match tycon.TypeAbbrev with
                | None -> lhsL @@-- memberLs
                | Some a -> (lhsL ^^ wordL(tagText "=")) --- (typeL a @@ memberLs)
            | a -> 
                let rhsL = tyconReprL (a, tycon) @@ memberLs
                (lhsL ^^ wordL(tagText "=")) @@-- rhsL
        reprL

    and entityL (entity: Entity) =
        if entity.IsModuleOrNamespace then
            moduleOrNamespaceL entity
        else
            tyconL entity

    and mexprL mtyp defs =
        let resL = mdefL defs
        let resL = if layoutTypes then resL @@- (wordL(tagText ":") @@- moduleOrNamespaceTypeL mtyp) else resL
        resL

    and mdefsL defs =
        wordL(tagText "Module Defs") @@-- aboveListL(List.map mdefL defs)

    and mdefL x =
        match x with
        | TMDefRec(_, _, tycons, mbinds, _) -> aboveListL ((tycons |> List.map tyconL) @ (mbinds |> List.map mbindL))
        | TMDefLet(bind, _) -> letL bind emptyL
        | TMDefDo(e, _) -> exprL e
        | TMDefOpens _ -> wordL (tagText "open ... ")
        | TMDefs defs -> mdefsL defs

    and mbindL x =
       match x with
       | ModuleOrNamespaceBinding.Binding bind -> letL bind emptyL
       | ModuleOrNamespaceBinding.Module(mspec, rhs) ->
        let titleL = wordL (tagText (if mspec.IsNamespace then "namespace" else "module")) ^^ (wordL (tagText mspec.DemangledModuleOrNamespaceName) |> stampL mspec.Stamp)
        titleL @@-- mdefL rhs

    and moduleOrNamespaceTypeL (mtyp: ModuleOrNamespaceType) =
        aboveListL [qlistL typeOfValL mtyp.AllValsAndMembers
                    qlistL tyconL mtyp.AllEntities]

    and moduleOrNamespaceL (ms: ModuleOrNamespace) =
        let header = wordL(tagText "module") ^^ (wordL (tagText ms.DemangledModuleOrNamespaceName) |> stampL ms.Stamp) ^^ wordL(tagText ":")
        let footer = wordL(tagText "end")
        let body = moduleOrNamespaceTypeL ms.ModuleOrNamespaceType
        (header @@-- body) @@ footer

    let implFileL (CheckedImplFile (signature=implFileTy; contents=implFileContents)) =
        aboveListL [(wordL(tagText "top implementation ")) @@-- mexprL implFileTy implFileContents]
        
    let implFilesL implFiles =
        aboveListL (List.map implFileL implFiles)

    let showType x = showL (typeL x)

    let showExpr x = showL (exprL x)

    let traitL x = auxTraitL SimplifyTypes.typeSimplificationInfo0 x

    let typarsL x = layoutTyparDecls x

//--------------------------------------------------------------------------
// Helpers related to type checking modules & namespaces
//--------------------------------------------------------------------------

let wrapModuleOrNamespaceType id cpath mtyp = 
    Construct.NewModuleOrNamespace (Some cpath) taccessPublic id XmlDoc.Empty [] (MaybeLazy.Strict mtyp)

let wrapModuleOrNamespaceTypeInNamespace id cpath mtyp = 
    let mspec = wrapModuleOrNamespaceType id cpath mtyp
    Construct.NewModuleOrNamespaceType (Namespace false) [ mspec ] [], mspec

let wrapModuleOrNamespaceContentsInNamespace isModule (id: Ident) (cpath: CompilationPath) mexpr =
    let mspec = wrapModuleOrNamespaceType id cpath (Construct.NewEmptyModuleOrNamespaceType (Namespace (not isModule)))
    TMDefRec (false, [], [], [ModuleOrNamespaceBinding.Module(mspec, mexpr)], id.idRange)

//--------------------------------------------------------------------------
// Data structures representing what gets hidden and what gets remapped
// when a module signature is applied to a module.
//--------------------------------------------------------------------------

type SignatureRepackageInfo = 
    { RepackagedVals: (ValRef * ValRef) list
      RepackagedEntities: (TyconRef * TyconRef) list }
    
    member remapInfo.ImplToSigMapping g = { TypeEquivEnv.EmptyWithNullChecks g with EquivTycons = TyconRefMap.OfList remapInfo.RepackagedEntities }
    static member Empty = { RepackagedVals = []; RepackagedEntities= [] } 

type SignatureHidingInfo = 
    { HiddenTycons: Zset<Tycon>
      HiddenTyconReprs: Zset<Tycon>
      HiddenVals: Zset<Val>
      HiddenRecdFields: Zset<RecdFieldRef>
      HiddenUnionCases: Zset<UnionCaseRef> }

    static member Empty = 
        { HiddenTycons = Zset.empty tyconOrder
          HiddenTyconReprs = Zset.empty tyconOrder
          HiddenVals = Zset.empty valOrder
          HiddenRecdFields = Zset.empty recdFieldRefOrder
          HiddenUnionCases = Zset.empty unionCaseRefOrder }

let addValRemap v vNew tmenv = 
    { tmenv with valRemap= tmenv.valRemap.Add v (mkLocalValRef vNew) }

let mkRepackageRemapping mrpi = 
    { valRemap = ValMap.OfList (mrpi.RepackagedVals |> List.map (fun (vref, x) -> vref.Deref, x))
      tpinst = emptyTyparInst
      tyconRefRemap = TyconRefMap.OfList mrpi.RepackagedEntities
      removeTraitSolutions = false }

//--------------------------------------------------------------------------
// Compute instances of the above for mty -> mty
//--------------------------------------------------------------------------

let accEntityRemap (msigty: ModuleOrNamespaceType) (entity: Entity) (mrpi, mhi) =
    let sigtyconOpt = (NameMap.tryFind entity.LogicalName msigty.AllEntitiesByCompiledAndLogicalMangledNames)
    match sigtyconOpt with 
    | None -> 
        // The type constructor is not present in the signature. Hence it is hidden. 
        let mhi = { mhi with HiddenTycons = Zset.add entity mhi.HiddenTycons }
        (mrpi, mhi) 
    | Some sigtycon -> 
        // The type constructor is in the signature. Hence record the repackage entry 
        let sigtcref = mkLocalTyconRef sigtycon
        let tcref = mkLocalTyconRef entity
        let mrpi = { mrpi with RepackagedEntities = ((tcref, sigtcref) :: mrpi.RepackagedEntities) }
        // OK, now look for hidden things 
        let mhi = 
            if (match entity.TypeReprInfo with TNoRepr -> false | _ -> true) && (match sigtycon.TypeReprInfo with TNoRepr -> true | _ -> false) then 
                // The type representation is absent in the signature, hence it is hidden 
                { mhi with HiddenTyconReprs = Zset.add entity mhi.HiddenTyconReprs } 
            else 
                // The type representation is present in the signature. 
                // Find the fields that have been hidden or which were non-public anyway. 
                let mhi = 
                    (entity.AllFieldsArray, mhi) ||> Array.foldBack (fun rfield mhi ->
                        match sigtycon.GetFieldByName(rfield.LogicalName) with 
                        | Some _ -> 
                            // The field is in the signature. Hence it is not hidden. 
                            mhi
                        | _ -> 
                            // The field is not in the signature. Hence it is regarded as hidden. 
                            let rfref = tcref.MakeNestedRecdFieldRef rfield
                            { mhi with HiddenRecdFields = Zset.add rfref mhi.HiddenRecdFields })
                        
                let mhi = 
                    (entity.UnionCasesAsList, mhi) ||> List.foldBack (fun ucase mhi ->
                        match sigtycon.GetUnionCaseByName ucase.LogicalName with 
                        | Some _ -> 
                            // The constructor is in the signature. Hence it is not hidden. 
                            mhi
                        | _ -> 
                            // The constructor is not in the signature. Hence it is regarded as hidden. 
                            let ucref = tcref.MakeNestedUnionCaseRef ucase
                            { mhi with HiddenUnionCases = Zset.add ucref mhi.HiddenUnionCases })
                mhi
        (mrpi, mhi) 

let accSubEntityRemap (msigty: ModuleOrNamespaceType) (entity: Entity) (mrpi, mhi) =
    let sigtyconOpt = (NameMap.tryFind entity.LogicalName msigty.AllEntitiesByCompiledAndLogicalMangledNames)
    match sigtyconOpt with 
    | None -> 
        // The type constructor is not present in the signature. Hence it is hidden. 
        let mhi = { mhi with HiddenTycons = Zset.add entity mhi.HiddenTycons }
        (mrpi, mhi) 
    | Some sigtycon -> 
        // The type constructor is in the signature. Hence record the repackage entry 
        let sigtcref = mkLocalTyconRef sigtycon
        let tcref = mkLocalTyconRef entity
        let mrpi = { mrpi with RepackagedEntities = ((tcref, sigtcref) :: mrpi.RepackagedEntities) }
        (mrpi, mhi) 

let valLinkageAEquiv g aenv (v1: Val) (v2: Val) = 
    (v1.GetLinkagePartialKey() = v2.GetLinkagePartialKey()) &&
    (if v1.IsMember && v2.IsMember then typeAEquivAux EraseAll g aenv v1.Type v2.Type else true)
    
let accValRemap g aenv (msigty: ModuleOrNamespaceType) (implVal: Val) (mrpi, mhi) =
    let implValKey = implVal.GetLinkagePartialKey()
    let sigValOpt = 
        msigty.AllValsAndMembersByPartialLinkageKey 
          |> MultiMap.find implValKey
          |> List.tryFind (fun sigVal -> valLinkageAEquiv g aenv implVal sigVal)
          
    let vref = mkLocalValRef implVal
    match sigValOpt with 
    | None -> 
        let mhi = { mhi with HiddenVals = Zset.add implVal mhi.HiddenVals }
        (mrpi, mhi) 
    | Some (sigVal: Val) -> 
        // The value is in the signature. Add the repackage entry. 
        let mrpi = { mrpi with RepackagedVals = (vref, mkLocalValRef sigVal) :: mrpi.RepackagedVals }
        (mrpi, mhi) 

let getCorrespondingSigTy nm (msigty: ModuleOrNamespaceType) = 
    match NameMap.tryFind nm msigty.AllEntitiesByCompiledAndLogicalMangledNames with 
    | None -> Construct.NewEmptyModuleOrNamespaceType ModuleOrType 
    | Some sigsubmodul -> sigsubmodul.ModuleOrNamespaceType

let rec accEntityRemapFromModuleOrNamespaceType (mty: ModuleOrNamespaceType) (msigty: ModuleOrNamespaceType) acc = 
    let acc = (mty.AllEntities, acc) ||> QueueList.foldBack (fun e acc -> accEntityRemapFromModuleOrNamespaceType e.ModuleOrNamespaceType (getCorrespondingSigTy e.LogicalName msigty) acc) 
    let acc = (mty.AllEntities, acc) ||> QueueList.foldBack (accEntityRemap msigty) 
    acc 

let rec accValRemapFromModuleOrNamespaceType g aenv (mty: ModuleOrNamespaceType) msigty acc = 
    let acc = (mty.AllEntities, acc) ||> QueueList.foldBack (fun e acc -> accValRemapFromModuleOrNamespaceType g aenv e.ModuleOrNamespaceType (getCorrespondingSigTy e.LogicalName msigty) acc) 
    let acc = (mty.AllValsAndMembers, acc) ||> QueueList.foldBack (accValRemap g aenv msigty) 
    acc 

let ComputeRemappingFromInferredSignatureToExplicitSignature g mty msigty = 
    let mrpi, _ as entityRemap = accEntityRemapFromModuleOrNamespaceType mty msigty (SignatureRepackageInfo.Empty, SignatureHidingInfo.Empty)  
    let aenv = mrpi.ImplToSigMapping g
    let valAndEntityRemap = accValRemapFromModuleOrNamespaceType g aenv mty msigty entityRemap
    valAndEntityRemap 

//--------------------------------------------------------------------------
// Compute instances of the above for mexpr -> mty
//--------------------------------------------------------------------------

/// At TMDefRec nodes abstract (virtual) vslots are effectively binders, even 
/// though they are tucked away inside the tycon. This helper function extracts the
/// virtual slots to aid with finding this babies.
let abstractSlotValRefsOfTycons (tycons: Tycon list) =  
    tycons 
    |> List.collect (fun tycon -> if tycon.IsFSharpObjectModelTycon then tycon.FSharpTyconRepresentationData.fsobjmodel_vslots else []) 

let abstractSlotValsOfTycons (tycons: Tycon list) =  
    abstractSlotValRefsOfTycons tycons 
    |> List.map (fun v -> v.Deref)

let rec accEntityRemapFromModuleOrNamespace msigty x acc = 
    match x with 
    | TMDefRec(_, _, tycons, mbinds, _) -> 
         let acc = (mbinds, acc) ||> List.foldBack (accEntityRemapFromModuleOrNamespaceBind msigty)
         let acc = (tycons, acc) ||> List.foldBack (accEntityRemap msigty) 
         let acc = (tycons, acc) ||> List.foldBack (fun e acc -> accEntityRemapFromModuleOrNamespaceType e.ModuleOrNamespaceType (getCorrespondingSigTy e.LogicalName msigty) acc) 
         acc
    | TMDefLet _ -> acc
    | TMDefOpens _ -> acc
    | TMDefDo _ -> acc
    | TMDefs defs -> accEntityRemapFromModuleOrNamespaceDefs msigty defs acc

and accEntityRemapFromModuleOrNamespaceDefs msigty mdefs acc = 
    List.foldBack (accEntityRemapFromModuleOrNamespace msigty) mdefs acc

and accEntityRemapFromModuleOrNamespaceBind msigty x acc = 
    match x with 
    | ModuleOrNamespaceBinding.Binding _ -> acc
    | ModuleOrNamespaceBinding.Module(mspec, def) ->
    accSubEntityRemap msigty mspec (accEntityRemapFromModuleOrNamespace (getCorrespondingSigTy mspec.LogicalName msigty) def acc)

let rec accValRemapFromModuleOrNamespace g aenv msigty x acc = 
    match x with 
    | TMDefRec(_, _, tycons, mbinds, _) -> 
         let acc = (mbinds, acc) ||> List.foldBack (accValRemapFromModuleOrNamespaceBind g aenv msigty)
         //  Abstract (virtual) vslots in the tycons at TMDefRec nodes are binders. They also need to be added to the remapping. 
         let vslotvs = abstractSlotValsOfTycons tycons
         let acc = (vslotvs, acc) ||> List.foldBack (accValRemap g aenv msigty)  
         acc
    | TMDefLet(bind, _) -> accValRemap g aenv msigty bind.Var acc
    | TMDefOpens _ -> acc
    | TMDefDo _ -> acc
    | TMDefs defs -> accValRemapFromModuleOrNamespaceDefs g aenv msigty defs acc

and accValRemapFromModuleOrNamespaceBind g aenv msigty x acc = 
    match x with 
    | ModuleOrNamespaceBinding.Binding bind -> accValRemap g aenv msigty bind.Var acc
    | ModuleOrNamespaceBinding.Module(mspec, def) ->
    accSubEntityRemap msigty mspec (accValRemapFromModuleOrNamespace g aenv (getCorrespondingSigTy mspec.LogicalName msigty) def acc)

and accValRemapFromModuleOrNamespaceDefs g aenv msigty mdefs acc = List.foldBack (accValRemapFromModuleOrNamespace g aenv msigty) mdefs acc

let ComputeRemappingFromImplementationToSignature g mdef msigty =  
    let mrpi, _ as entityRemap = accEntityRemapFromModuleOrNamespace msigty mdef (SignatureRepackageInfo.Empty, SignatureHidingInfo.Empty) 
    let aenv = mrpi.ImplToSigMapping g
    
    let valAndEntityRemap = accValRemapFromModuleOrNamespace g aenv msigty mdef entityRemap
    valAndEntityRemap

//--------------------------------------------------------------------------
// Compute instances of the above for the assembly boundary
//--------------------------------------------------------------------------

let accTyconHidingInfoAtAssemblyBoundary (tycon: Tycon) mhi =
    if not (canAccessFromEverywhere tycon.Accessibility) then 
        // The type constructor is not public, hence hidden at the assembly boundary. 
        { mhi with HiddenTycons = Zset.add tycon mhi.HiddenTycons } 
    elif not (canAccessFromEverywhere tycon.TypeReprAccessibility) then 
        { mhi with HiddenTyconReprs = Zset.add tycon mhi.HiddenTyconReprs } 
    else 
        let mhi = 
            (tycon.AllFieldsArray, mhi) ||> Array.foldBack (fun rfield mhi ->
                if not (canAccessFromEverywhere rfield.Accessibility) then 
                    let tcref = mkLocalTyconRef tycon
                    let rfref = tcref.MakeNestedRecdFieldRef rfield
                    { mhi with HiddenRecdFields = Zset.add rfref mhi.HiddenRecdFields } 
                else mhi)
        let mhi = 
            (tycon.UnionCasesAsList, mhi) ||> List.foldBack (fun ucase mhi ->
                if not (canAccessFromEverywhere ucase.Accessibility) then 
                    let tcref = mkLocalTyconRef tycon
                    let ucref = tcref.MakeNestedUnionCaseRef ucase
                    { mhi with HiddenUnionCases = Zset.add ucref mhi.HiddenUnionCases } 
                else mhi)
        mhi

// Collect up the values hidden at the assembly boundary. This is used by IsHiddenVal to 
// determine if something is considered hidden. This is used in turn to eliminate optimization
// information at the assembly boundary and to decide to label things as "internal".
let accValHidingInfoAtAssemblyBoundary (vspec: Val) mhi =
    if // anything labelled "internal" or more restrictive is considered to be hidden at the assembly boundary
       not (canAccessFromEverywhere vspec.Accessibility) || 
       // compiler generated members for class function 'let' bindings are considered to be hidden at the assembly boundary
       vspec.IsIncrClassGeneratedMember ||                     
       // anything that's not a module or member binding gets assembly visibility
       not vspec.IsMemberOrModuleBinding then 
        // The value is not public, hence hidden at the assembly boundary. 
        { mhi with HiddenVals = Zset.add vspec mhi.HiddenVals } 
    else 
        mhi

let rec accModuleOrNamespaceHidingInfoAtAssemblyBoundary mty acc = 
    let acc = QueueList.foldBack (fun (e: Entity) acc -> accModuleOrNamespaceHidingInfoAtAssemblyBoundary e.ModuleOrNamespaceType acc) mty.AllEntities acc
    let acc = QueueList.foldBack accTyconHidingInfoAtAssemblyBoundary mty.AllEntities acc
    let acc = QueueList.foldBack accValHidingInfoAtAssemblyBoundary mty.AllValsAndMembers acc
    acc 

let ComputeSignatureHidingInfoAtAssemblyBoundary mty acc = 
    accModuleOrNamespaceHidingInfoAtAssemblyBoundary mty acc

let rec accImplHidingInfoAtAssemblyBoundary mdef acc = 
    match mdef with 
    | TMDefRec(_isRec, _opens, tycons, mbinds, _m) -> 
        let acc = List.foldBack accTyconHidingInfoAtAssemblyBoundary tycons acc
        let acc =
            (mbinds, acc) ||> List.foldBack (fun mbind acc ->
                match mbind with
                | ModuleOrNamespaceBinding.Binding bind -> 
                    accValHidingInfoAtAssemblyBoundary bind.Var acc
                | ModuleOrNamespaceBinding.Module(_mspec, def) -> 
                    accImplHidingInfoAtAssemblyBoundary def acc)
        acc

    | TMDefOpens _openDecls ->  acc

    | TMDefLet(bind, _m) -> accValHidingInfoAtAssemblyBoundary bind.Var acc

    | TMDefDo _ -> acc

    | TMDefs defs -> List.foldBack accImplHidingInfoAtAssemblyBoundary defs acc

let ComputeImplementationHidingInfoAtAssemblyBoundary mty acc = 
    accImplHidingInfoAtAssemblyBoundary mty acc

let DoRemap setF remapF = 
    let rec remap mrmi x =

        match mrmi with
         | [] -> x
         | (rpi, mhi) :: rest ->
            // Explicitly hidden?
            if Zset.contains x (setF mhi) then
                x
            else
                remap rest (remapF rpi x)
    fun mrmi x -> remap mrmi x

let DoRemapTycon mrmi x = DoRemap (fun mhi -> mhi.HiddenTycons) (fun rpi x -> (remapTyconRef rpi.tyconRefRemap (mkLocalTyconRef x)).Deref) mrmi x

let DoRemapVal mrmi x = DoRemap (fun mhi -> mhi.HiddenVals) (fun rpi x -> (remapValRef rpi (mkLocalValRef x)).Deref) mrmi x 

//--------------------------------------------------------------------------
// Compute instances of the above for mexpr -> mty
//--------------------------------------------------------------------------
let IsHidden setF accessF remapF = 
    let rec check mrmi x =
        // Internal/private?
        not (canAccessFromEverywhere (accessF x)) ||
        (match mrmi with
         | [] -> false // Ah! we escaped to freedom!
         | (rpi, mhi) :: rest ->
            // Explicitly hidden?
            Zset.contains x (setF mhi) ||
            // Recurse...
            check rest (remapF rpi x))
    check

let IsHiddenTycon mrmi x = IsHidden (fun mhi -> mhi.HiddenTycons) (fun tc -> tc.Accessibility) (fun rpi x -> (remapTyconRef rpi.tyconRefRemap (mkLocalTyconRef x)).Deref) mrmi x

let IsHiddenTyconRepr mrmi x = IsHidden (fun mhi -> mhi.HiddenTyconReprs) (fun v -> v.TypeReprAccessibility) (fun rpi x -> (remapTyconRef rpi.tyconRefRemap (mkLocalTyconRef x)).Deref) mrmi x

let IsHiddenVal mrmi x = IsHidden (fun mhi -> mhi.HiddenVals) (fun v -> v.Accessibility) (fun rpi x -> (remapValRef rpi (mkLocalValRef x)).Deref) mrmi x 

let IsHiddenRecdField mrmi x = IsHidden (fun mhi -> mhi.HiddenRecdFields) (fun rfref -> rfref.RecdField.Accessibility) (fun rpi x -> remapRecdFieldRef rpi.tyconRefRemap x) mrmi x 

//--------------------------------------------------------------------------
// Generic operations on module types
//--------------------------------------------------------------------------

let foldModuleOrNamespaceTy ft fv mty acc = 
    let rec go mty acc = 
        let acc = QueueList.foldBack (fun (e: Entity) acc -> go e.ModuleOrNamespaceType acc) mty.AllEntities acc
        let acc = QueueList.foldBack ft mty.AllEntities acc
        let acc = QueueList.foldBack fv mty.AllValsAndMembers acc
        acc
    go mty acc

let allValsOfModuleOrNamespaceTy m = foldModuleOrNamespaceTy (fun _ acc -> acc) (fun v acc -> v :: acc) m []
let allEntitiesOfModuleOrNamespaceTy m = foldModuleOrNamespaceTy (fun ft acc -> ft :: acc) (fun _ acc -> acc) m []

//---------------------------------------------------------------------------
// Free variables in terms. Are all constructs public accessible?
//---------------------------------------------------------------------------
 
let isPublicVal (lv: Val) = (lv.Accessibility = taccessPublic)
let isPublicUnionCase (ucr: UnionCaseRef) = (ucr.UnionCase.Accessibility = taccessPublic)
let isPublicRecdField (rfr: RecdFieldRef) = (rfr.RecdField.Accessibility = taccessPublic)
let isPublicTycon (tcref: Tycon) = (tcref.Accessibility = taccessPublic)

let freeVarsAllPublic fvs = 
    // Are any non-public items used in the expr (which corresponded to the fvs)?
    // Recall, taccess occurs in:
    //      EntityData has ReprAccessibility and Accessibility
    //      UnionCase has Accessibility
    //      RecdField has Accessibility
    //      ValData has Accessibility
    // The freevars and FreeTyvars collect local constructs.
    // Here, we test that all those constructs are public.
    //
    // CODE REVIEW:
    // What about non-local vals. This fix assumes non-local vals must be public. OK?
    Zset.forall isPublicVal fvs.FreeLocals &&
    Zset.forall isPublicUnionCase fvs.FreeUnionCases &&
    Zset.forall isPublicRecdField fvs.FreeRecdFields &&
    Zset.forall isPublicTycon fvs.FreeTyvars.FreeTycons

let freeTyvarsAllPublic tyvars = 
    Zset.forall isPublicTycon tyvars.FreeTycons

/// Detect the subset of match expressions we process in a linear way (i.e. using tailcalls, rather than
/// unbounded stack)
///   -- if then else
///   -- match e with pat[vs] -> e1[vs] | _ -> e2

[<return: Struct>]
let (|LinearMatchExpr|_|) expr = 
    match expr with 
    | Expr.Match (sp, m, dtree, [|tg1;(TTarget([], e2, _))|], m2, ty) -> ValueSome(sp, m, dtree, tg1, e2, m2, ty)
    | _ -> ValueNone
    
let rebuildLinearMatchExpr (sp, m, dtree, tg1, e2, m2, ty) = 
    primMkMatch (sp, m, dtree, [|tg1;(TTarget([], e2, None))|], m2, ty)

/// Detect a subset of 'Expr.Op' expressions we process in a linear way (i.e. using tailcalls, rather than
/// unbounded stack). Only covers Cons(args,Cons(args,Cons(args,Cons(args,...._)))).
[<return: Struct>]
let (|LinearOpExpr|_|) expr = 
    match expr with 
    | Expr.Op (TOp.UnionCase _ as op, tinst, args, m) when not args.IsEmpty -> 
        let argsFront, argLast = List.frontAndBack args
        ValueSome (op, tinst, argsFront, argLast, m)
    | _ -> ValueNone
    
let rebuildLinearOpExpr (op, tinst, argsFront, argLast, m) = 
    Expr.Op (op, tinst, argsFront@[argLast], m)

//---------------------------------------------------------------------------
// Free variables in terms. All binders are distinct.
//---------------------------------------------------------------------------

let emptyFreeVars =  
  { UsesMethodLocalConstructs=false
    UsesUnboundRethrow=false
    FreeLocalTyconReprs=emptyFreeTycons
    FreeLocals=emptyFreeLocals
    FreeTyvars=emptyFreeTyvars
    FreeRecdFields = emptyFreeRecdFields
    FreeUnionCases = emptyFreeUnionCases}

let unionFreeVars fvs1 fvs2 = 
  if fvs1 === emptyFreeVars then fvs2 else 
  if fvs2 === emptyFreeVars then fvs1 else
  { FreeLocals = unionFreeLocals fvs1.FreeLocals fvs2.FreeLocals
    FreeTyvars = unionFreeTyvars fvs1.FreeTyvars fvs2.FreeTyvars
    UsesMethodLocalConstructs = fvs1.UsesMethodLocalConstructs || fvs2.UsesMethodLocalConstructs
    UsesUnboundRethrow = fvs1.UsesUnboundRethrow || fvs2.UsesUnboundRethrow
    FreeLocalTyconReprs = unionFreeTycons fvs1.FreeLocalTyconReprs fvs2.FreeLocalTyconReprs
    FreeRecdFields = unionFreeRecdFields fvs1.FreeRecdFields fvs2.FreeRecdFields
    FreeUnionCases = unionFreeUnionCases fvs1.FreeUnionCases fvs2.FreeUnionCases }

let inline accFreeTyvars (opts: FreeVarOptions) f v acc =
    if not opts.collectInTypes then acc else
    let ftyvs = acc.FreeTyvars
    let ftyvs' = f opts v ftyvs
    if ftyvs === ftyvs' then acc else 
    { acc with FreeTyvars = ftyvs' }

let accFreeVarsInTy opts ty acc = accFreeTyvars opts accFreeInType ty acc
let accFreeVarsInTys opts tys acc = if isNil tys then acc else accFreeTyvars opts accFreeInTypes tys acc
let accFreevarsInTycon opts tcref acc = accFreeTyvars opts accFreeTycon tcref acc
let accFreevarsInVal opts v acc = accFreeTyvars opts accFreeInVal v acc
    
let accFreeVarsInTraitSln opts tys acc = accFreeTyvars opts accFreeInTraitSln tys acc 

let accFreeVarsInTraitInfo opts tys acc = accFreeTyvars opts accFreeInTrait tys acc 

let boundLocalVal opts v fvs =
    if not opts.includeLocals then fvs else
    let fvs = accFreevarsInVal opts v fvs
    if not (Zset.contains v fvs.FreeLocals) then fvs
    else {fvs with FreeLocals= Zset.remove v fvs.FreeLocals} 

let boundProtect fvs =
    if fvs.UsesMethodLocalConstructs then {fvs with UsesMethodLocalConstructs = false} else fvs

let accUsesFunctionLocalConstructs flg fvs = 
    if flg && not fvs.UsesMethodLocalConstructs then {fvs with UsesMethodLocalConstructs = true} 
    else fvs 

let bound_rethrow fvs =
    if fvs.UsesUnboundRethrow then {fvs with UsesUnboundRethrow = false} else fvs  

let accUsesRethrow flg fvs = 
    if flg && not fvs.UsesUnboundRethrow then {fvs with UsesUnboundRethrow = true} 
    else fvs 

let boundLocalVals opts vs fvs = List.foldBack (boundLocalVal opts) vs fvs

let bindLhs opts (bind: Binding) fvs = boundLocalVal opts bind.Var fvs

let freeVarsCacheCompute opts cache f = if opts.canCache then cached cache f else f()

let tryGetFreeVarsCacheValue opts cache =
    if opts.canCache then tryGetCacheValue cache
    else ValueNone

let accFreeLocalVal opts v fvs =
    if not opts.includeLocals then fvs else
    if Zset.contains v fvs.FreeLocals then fvs 
    else 
        let fvs = accFreevarsInVal opts v fvs
        {fvs with FreeLocals=Zset.add v fvs.FreeLocals}

let accFreeInValFlags opts flag acc =
    let isMethLocal = 
        match flag with 
        | VSlotDirectCall 
        | CtorValUsedAsSelfInit 
        | CtorValUsedAsSuperInit -> true 
        | PossibleConstrainedCall _
        | NormalValUse -> false
    let acc = accUsesFunctionLocalConstructs isMethLocal acc
    match flag with 
    | PossibleConstrainedCall ty -> accFreeTyvars opts accFreeInType ty acc
    | _ -> acc
    
let accLocalTyconRepr opts b fvs = 
    if not opts.includeLocalTyconReprs then fvs else
    if Zset.contains b fvs.FreeLocalTyconReprs then fvs
    else { fvs with FreeLocalTyconReprs = Zset.add b fvs.FreeLocalTyconReprs }

let inline accFreeExnRef _exnc fvs = fvs // Note: this exnc (TyconRef) should be collected the surround types, e.g. tinst of Expr.Op

let rec accBindRhs opts (TBind(_, repr, _)) acc = accFreeInExpr opts repr acc
          
and accFreeInSwitchCases opts csl dflt (acc: FreeVars) =
    Option.foldBack (accFreeInDecisionTree opts) dflt (List.foldBack (accFreeInSwitchCase opts) csl acc)
 
and accFreeInSwitchCase opts (TCase(discrim, dtree)) acc = 
    accFreeInDecisionTree opts dtree (accFreeInTest opts discrim acc)

and accFreeInTest (opts: FreeVarOptions) discrim acc = 
    match discrim with 
    | DecisionTreeTest.UnionCase(ucref, tinst) -> accFreeUnionCaseRef opts ucref (accFreeVarsInTys opts tinst acc)
    | DecisionTreeTest.ArrayLength(_, ty) -> accFreeVarsInTy opts ty acc
    | DecisionTreeTest.Const _
    | DecisionTreeTest.IsNull -> acc
    | DecisionTreeTest.IsInst (srcTy, tgtTy) -> accFreeVarsInTy opts srcTy (accFreeVarsInTy opts tgtTy acc)
    | DecisionTreeTest.ActivePatternCase (exp, tys, _, activePatIdentity, _, _) -> 
        accFreeInExpr opts exp 
            (accFreeVarsInTys opts tys 
                (Option.foldBack (fun (vref, tinst) acc -> accFreeValRef opts vref (accFreeVarsInTys opts tinst acc)) activePatIdentity acc))
    | DecisionTreeTest.Error _ -> acc

and accFreeInDecisionTree opts x (acc: FreeVars) =
    match x with 
    | TDSwitch(e1, csl, dflt, _) -> accFreeInExpr opts e1 (accFreeInSwitchCases opts csl dflt acc)
    | TDSuccess (es, _) -> accFreeInFlatExprs opts es acc
    | TDBind (bind, body) -> unionFreeVars (bindLhs opts bind (accBindRhs opts bind (freeInDecisionTree opts body))) acc

and accUsedRecdOrUnionTyconRepr opts (tc: Tycon) fvs =
    if (match tc.TypeReprInfo with TFSharpTyconRepr _ -> true | _ -> false) then
        accLocalTyconRepr opts tc fvs
    else
        fvs

and accFreeUnionCaseRef opts ucref fvs =   
    if not opts.includeUnionCases then fvs else
    if Zset.contains ucref fvs.FreeUnionCases then fvs 
    else
        let fvs = fvs |> accUsedRecdOrUnionTyconRepr opts ucref.Tycon
        let fvs = fvs |> accFreevarsInTycon opts ucref.TyconRef
        { fvs with FreeUnionCases = Zset.add ucref fvs.FreeUnionCases } 

and accFreeRecdFieldRef opts rfref fvs = 
    if not opts.includeRecdFields then fvs else
    if Zset.contains rfref fvs.FreeRecdFields then fvs 
    else 
        let fvs = fvs |> accUsedRecdOrUnionTyconRepr opts rfref.Tycon
        let fvs = fvs |> accFreevarsInTycon opts rfref.TyconRef 
        { fvs with FreeRecdFields = Zset.add rfref fvs.FreeRecdFields } 

and accFreeValRef opts (vref: ValRef) fvs = 
    match vref.IsLocalRef with 
    | true -> accFreeLocalVal opts vref.ResolvedTarget fvs
    // non-local values do not contain free variables 
    | _ -> fvs

and accFreeInMethod opts (TObjExprMethod(slotsig, _attribs, tps, tmvs, e, _)) acc =
    accFreeInSlotSig opts slotsig
     (unionFreeVars (accFreeTyvars opts boundTypars tps (List.foldBack (boundLocalVals opts) tmvs (freeInExpr opts e))) acc)

and accFreeInMethods opts methods acc = 
    List.foldBack (accFreeInMethod opts) methods acc

and accFreeInInterfaceImpl opts (ty, overrides) acc = 
    accFreeVarsInTy opts ty (accFreeInMethods opts overrides acc)

and accFreeInExpr (opts: FreeVarOptions) x acc = 
    match x with
    | Expr.Let _ -> accFreeInExprLinear opts x acc id
    | _ -> accFreeInExprNonLinear opts x acc
      
and accFreeInExprLinear (opts: FreeVarOptions) x acc contf =   
    // for nested let-bindings, we need to continue after the whole let-binding is processed 
    match x with
    | Expr.Let (bind, e, _, cache) ->
        match tryGetFreeVarsCacheValue opts cache with
        | ValueSome free -> contf (unionFreeVars free acc)
        | _ ->
            accFreeInExprLinear opts e emptyFreeVars (contf << (fun free ->
              unionFreeVars (freeVarsCacheCompute opts cache (fun () -> bindLhs opts bind (accBindRhs opts bind free))) acc
            ))
    | _ -> 
        // No longer linear expr
        contf (accFreeInExpr opts x acc)
    
and accFreeInExprNonLinear opts x acc =
    
    match opts.stackGuard with
    | None -> accFreeInExprNonLinearImpl opts x acc
    | Some stackGuard -> stackGuard.Guard (fun () -> accFreeInExprNonLinearImpl opts x acc)

and accFreeInExprNonLinearImpl opts x acc =

    match x with
    // BINDING CONSTRUCTS
    | Expr.Lambda (_, ctorThisValOpt, baseValOpt, vs, bodyExpr, _, bodyTy) -> 
        unionFreeVars 
                (Option.foldBack (boundLocalVal opts) ctorThisValOpt 
                   (Option.foldBack (boundLocalVal opts) baseValOpt 
                     (boundLocalVals opts vs 
                         (accFreeVarsInTy opts bodyTy 
                             (freeInExpr opts bodyExpr)))))
            acc

    | Expr.TyLambda (_, vs, bodyExpr, _, bodyTy) ->
        unionFreeVars (accFreeTyvars opts boundTypars vs (accFreeVarsInTy opts bodyTy (freeInExpr opts bodyExpr))) acc

    | Expr.TyChoose (vs, bodyExpr, _) ->
        unionFreeVars (accFreeTyvars opts boundTypars vs (freeInExpr opts bodyExpr)) acc

    | Expr.LetRec (binds, bodyExpr, _, cache) ->
        unionFreeVars (freeVarsCacheCompute opts cache (fun () -> List.foldBack (bindLhs opts) binds (List.foldBack (accBindRhs opts) binds (freeInExpr opts bodyExpr)))) acc

    | Expr.Let _ -> 
        failwith "unreachable - linear expr"

    | Expr.Obj (_, ty, basev, basecall, overrides, iimpls, _) ->  
        unionFreeVars 
           (boundProtect
              (Option.foldBack (boundLocalVal opts) basev
                (accFreeVarsInTy opts ty
                   (accFreeInExpr opts basecall
                      (accFreeInMethods opts overrides 
                         (List.foldBack (accFreeInInterfaceImpl opts) iimpls emptyFreeVars))))))
           acc  

    // NON-BINDING CONSTRUCTS 
    | Expr.Const _ -> acc

    | Expr.Val (lvr, flags, _) ->  
        accFreeInValFlags opts flags (accFreeValRef opts lvr acc)

    | Expr.Quote (ast, dataCell, _, _, ty) ->  
        match dataCell.Value with 
        | Some (_, (_, argTypes, argExprs, _data)) ->
            accFreeInExpr opts ast 
                (accFreeInExprs opts argExprs
                   (accFreeVarsInTys opts argTypes
                      (accFreeVarsInTy opts ty acc))) 

        | None ->
            accFreeInExpr opts ast (accFreeVarsInTy opts ty acc)

    | Expr.App (f0, f0ty, tyargs, args, _) -> 
        accFreeVarsInTy opts f0ty
          (accFreeInExpr opts f0
             (accFreeVarsInTys opts tyargs
                (accFreeInExprs opts args acc)))

    | Expr.Link eref ->
        accFreeInExpr opts eref.Value acc

    | Expr.Sequential (expr1, expr2, _, _) -> 
        let acc = accFreeInExpr opts expr1 acc
        // tail-call - linear expression
        accFreeInExpr opts expr2 acc 

    | Expr.StaticOptimization (_, expr2, expr3, _) -> 
        accFreeInExpr opts expr2 (accFreeInExpr opts expr3 acc)

    | Expr.Match (_, _, dtree, targets, _, _) -> 
        match x with 
        // Handle if-then-else
        | LinearMatchExpr(_, _, dtree, target, bodyExpr, _, _) ->
            let acc = accFreeInDecisionTree opts dtree acc
            let acc = accFreeInTarget opts target acc
            accFreeInExpr opts bodyExpr acc  // tailcall

        | _ -> 
            let acc = accFreeInDecisionTree opts dtree acc
            accFreeInTargets opts targets acc
            
    | Expr.Op (TOp.TryWith _, tinst, [expr1; expr2; expr3], _) ->
        unionFreeVars 
          (accFreeVarsInTys opts tinst
            (accFreeInExprs opts [expr1; expr2] acc))
          (bound_rethrow (accFreeInExpr opts expr3 emptyFreeVars))

    | Expr.Op (op, tinst, args, _) -> 
         let acc = accFreeInOp opts op acc
         let acc = accFreeVarsInTys opts tinst acc
         accFreeInExprs opts args acc

    | Expr.WitnessArg (traitInfo, _) ->
         accFreeVarsInTraitInfo opts traitInfo acc

    | Expr.DebugPoint (_, innerExpr) ->
         accFreeInExpr opts innerExpr acc

and accFreeInOp opts op acc =
    match op with

    // Things containing no references
    | TOp.Bytes _ 
    | TOp.UInt16s _ 
    | TOp.TryWith _
    | TOp.TryFinally _ 
    | TOp.IntegerForLoop _ 
    | TOp.Coerce 
    | TOp.RefAddrGet _
    | TOp.Array 
    | TOp.While _
    | TOp.Goto _ | TOp.Label _ | TOp.Return 
    | TOp.TupleFieldGet _ -> acc

    | TOp.Tuple tupInfo -> 
        accFreeTyvars opts accFreeInTupInfo tupInfo acc

    | TOp.AnonRecd anonInfo 
    | TOp.AnonRecdGet (anonInfo, _) -> 
        accFreeTyvars opts accFreeInTupInfo anonInfo.TupInfo acc
    
    | TOp.UnionCaseTagGet tcref -> 
        accUsedRecdOrUnionTyconRepr opts tcref.Deref acc
    
    // Things containing just a union case reference
    | TOp.UnionCaseProof ucref 
    | TOp.UnionCase ucref 
    | TOp.UnionCaseFieldGetAddr (ucref, _, _) 
    | TOp.UnionCaseFieldGet (ucref, _) 
    | TOp.UnionCaseFieldSet (ucref, _) -> 
        accFreeUnionCaseRef opts ucref acc

    // Things containing just an exception reference
    | TOp.ExnConstr ecref 
    | TOp.ExnFieldGet (ecref, _) 
    | TOp.ExnFieldSet (ecref, _) -> 
        accFreeExnRef ecref acc

    | TOp.ValFieldGet fref 
    | TOp.ValFieldGetAddr (fref, _) 
    | TOp.ValFieldSet fref -> 
        accFreeRecdFieldRef opts fref acc

    | TOp.Recd (kind, tcref) -> 
        let acc = accUsesFunctionLocalConstructs (kind = RecdExprIsObjInit) acc
        (accUsedRecdOrUnionTyconRepr opts tcref.Deref (accFreeTyvars opts accFreeTycon tcref acc)) 

    | TOp.ILAsm (_, retTypes) ->  
        accFreeVarsInTys opts retTypes acc
    
    | TOp.Reraise -> 
        accUsesRethrow true acc

    | TOp.TraitCall (TTrait(tys, _, _, argTys, retTy, _, sln)) -> 
        Option.foldBack (accFreeVarsInTraitSln opts) sln.Value
           (accFreeVarsInTys opts tys 
             (accFreeVarsInTys opts argTys 
               (Option.foldBack (accFreeVarsInTy opts) retTy acc)))

    | TOp.LValueOp (_, vref) -> 
        accFreeValRef opts vref acc

    | TOp.ILCall (_, isProtected, _, _, valUseFlag, _, _, _, enclTypeInst, methInst, retTypes) ->
       accFreeVarsInTys opts enclTypeInst 
         (accFreeVarsInTys opts methInst  
           (accFreeInValFlags opts valUseFlag
             (accFreeVarsInTys opts retTypes 
               (accUsesFunctionLocalConstructs isProtected acc))))

and accFreeInTargets opts targets acc = 
    Array.foldBack (accFreeInTarget opts) targets acc

and accFreeInTarget opts (TTarget(vs, expr, flags)) acc = 
    match flags with 
    | None -> List.foldBack (boundLocalVal opts) vs (accFreeInExpr opts expr acc)
    | Some xs -> List.foldBack2 (fun v isStateVar acc -> if isStateVar then acc else boundLocalVal opts v acc) vs xs (accFreeInExpr opts expr acc)

and accFreeInFlatExprs opts (exprs: Exprs) acc = List.foldBack (accFreeInExpr opts) exprs acc

and accFreeInExprs opts (exprs: Exprs) acc = 
    match exprs with 
    | [] -> acc 
    | [h]-> 
        // tailcall - e.g. Cons(x, Cons(x2, .......Cons(x1000000, Nil))) and [| x1; .... ; x1000000 |]
        accFreeInExpr opts h acc
    | h :: t -> 
        let acc = accFreeInExpr opts h acc
        accFreeInExprs opts t acc

and accFreeInSlotSig opts (TSlotSig(_, ty, _, _, _, _)) acc = 
    accFreeVarsInTy opts ty acc
 
and freeInDecisionTree opts dtree = 
    accFreeInDecisionTree opts dtree emptyFreeVars

and freeInExpr opts expr = 
    accFreeInExpr opts expr emptyFreeVars

// Note: these are only an approximation - they are currently used only by the optimizer  
let rec accFreeInModuleOrNamespace opts mexpr acc = 
    match mexpr with 
    | TMDefRec(_, _, _, mbinds, _) -> List.foldBack (accFreeInModuleOrNamespaceBind opts) mbinds acc
    | TMDefLet(bind, _) -> accBindRhs opts bind acc
    | TMDefDo(e, _) -> accFreeInExpr opts e acc
    | TMDefOpens _ -> acc
    | TMDefs defs -> accFreeInModuleOrNamespaces opts defs acc

and accFreeInModuleOrNamespaceBind opts mbind acc = 
    match mbind with 
    | ModuleOrNamespaceBinding.Binding bind -> accBindRhs opts bind acc
    | ModuleOrNamespaceBinding.Module (_, def) -> accFreeInModuleOrNamespace opts def acc

and accFreeInModuleOrNamespaces opts mexprs acc = 
    List.foldBack (accFreeInModuleOrNamespace opts) mexprs acc

let freeInBindingRhs opts bind = 
    accBindRhs opts bind emptyFreeVars

let freeInModuleOrNamespace opts mdef = 
    accFreeInModuleOrNamespace opts mdef emptyFreeVars

//---------------------------------------------------------------------------
// Destruct - rarely needed
//---------------------------------------------------------------------------

let rec stripLambda (expr, ty) = 
    match expr with 
    | Expr.Lambda (_, ctorThisValOpt, baseValOpt, v, bodyExpr, _, bodyTy) -> 
        if Option.isSome ctorThisValOpt then errorR(InternalError("skipping ctorThisValOpt", expr.Range))
        if Option.isSome baseValOpt then errorR(InternalError("skipping baseValOpt", expr.Range))
        let vs', bodyExpr', bodyTy' = stripLambda (bodyExpr, bodyTy)
        (v :: vs', bodyExpr', bodyTy') 
    | _ -> ([], expr, ty)

let rec stripLambdaN n expr = 
    assert (n >= 0)
    match expr with 
    | Expr.Lambda (_, ctorThisValOpt, baseValOpt, v, bodyExpr, _, _) when n > 0 -> 
        if Option.isSome ctorThisValOpt then errorR(InternalError("skipping ctorThisValOpt", expr.Range))
        if Option.isSome baseValOpt then errorR(InternalError("skipping baseValOpt", expr.Range))
        let vs, bodyExpr', remaining = stripLambdaN (n-1) bodyExpr
        (v :: vs, bodyExpr', remaining) 
    | _ -> ([], expr, n)

let tryStripLambdaN n expr = 
    match expr with
    | Expr.Lambda (_, None, None, _, _, _, _) -> 
        let argvsl, bodyExpr, remaining = stripLambdaN n expr
        if remaining = 0 then Some (argvsl, bodyExpr)
        else None
    | _ -> None

let stripTopLambda (expr, exprTy) =
    let tps, taue, tauty =
        match expr with
        | Expr.TyLambda (_, tps, body, _, bodyTy) -> tps, body, bodyTy
        | _ -> [], expr, exprTy
    let vs, body, bodyTy = stripLambda (taue, tauty)
    tps, vs, body, bodyTy

[<RequireQualifiedAccess>]
type AllowTypeDirectedDetupling = Yes | No

// This is used to infer arities of expressions 
// i.e. base the chosen arity on the syntactic expression shape and type of arguments 
let InferValReprInfoOfExpr g allowTypeDirectedDetupling ty partialArgAttribsL retAttribs expr = 
    let rec stripLambda_notypes e = 
        match stripDebugPoints e with 
        | Expr.Lambda (_, _, _, vs, b, _, _) -> 
            let vs', b' = stripLambda_notypes b
            (vs :: vs', b') 
        | Expr.TyChoose (_, b, _) ->
            stripLambda_notypes b 
        | _ -> ([], e)

    let stripTopLambdaNoTypes e =
        let tps, taue =
            match stripDebugPoints e with
            | Expr.TyLambda (_, tps, b, _, _) -> tps, b
            | _ -> [], e
        let vs, body = stripLambda_notypes taue
        tps, vs, body

    let tps, vsl, _ = stripTopLambdaNoTypes expr
    let fun_arity = vsl.Length
    let dtys, _ = stripFunTyN g fun_arity (snd (tryDestForallTy g ty))
    let partialArgAttribsL = Array.ofList partialArgAttribsL
    assert (List.length vsl = List.length dtys)
        
    let curriedArgInfos =
        (vsl, dtys) ||> List.mapi2 (fun i vs ty ->
            let partialAttribs = if i < partialArgAttribsL.Length then partialArgAttribsL[i] else []
            let tys = 
                match allowTypeDirectedDetupling with
                | AllowTypeDirectedDetupling.No -> [ty] 
                | AllowTypeDirectedDetupling.Yes -> 
                    if (i = 0 && isUnitTy g ty) then [] 
                    else tryDestRefTupleTy g ty
            let ids = 
                if vs.Length = tys.Length then vs |> List.map (fun v -> Some v.Id)
                else tys |> List.map (fun _ -> None)
            let attribs = 
                if partialAttribs.Length = tys.Length then partialAttribs 
                else tys |> List.map (fun _ -> [])
            (ids, attribs) ||> List.map2 (fun id attribs -> { Name = id; Attribs = attribs; OtherRange = None }: ArgReprInfo ))

    let retInfo: ArgReprInfo = { Attribs = retAttribs; Name = None; OtherRange = None }
    let info = ValReprInfo (ValReprInfo.InferTyparInfo tps, curriedArgInfos, retInfo)
    if ValReprInfo.IsEmpty info then ValReprInfo.emptyValData else info

let InferValReprInfoOfBinding g allowTypeDirectedDetupling (v: Val) expr = 
    match v.ValReprInfo with
    | Some info -> info
    | None -> InferValReprInfoOfExpr g allowTypeDirectedDetupling v.Type [] [] expr

//-------------------------------------------------------------------------
// Check if constraints are satisfied that allow us to use more optimized
// implementations
//------------------------------------------------------------------------- 

let underlyingTypeOfEnumTy (g: TcGlobals) ty = 
    assert(isEnumTy g ty)
    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata info -> info.UnderlyingTypeOfEnum()
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, tdef)) -> 

        let info = computeILEnumInfo (tdef.Name, tdef.Fields)
        let ilTy = getTyOfILEnumInfo info
        match ilTy.TypeSpec.Name with 
        | "System.Byte" -> g.byte_ty
        | "System.SByte" -> g.sbyte_ty
        | "System.Int16" -> g.int16_ty
        | "System.Int32" -> g.int32_ty
        | "System.Int64" -> g.int64_ty
        | "System.UInt16" -> g.uint16_ty
        | "System.UInt32" -> g.uint32_ty
        | "System.UInt64" -> g.uint64_ty
        | "System.Single" -> g.float32_ty
        | "System.Double" -> g.float_ty
        | "System.Char" -> g.char_ty
        | "System.Boolean" -> g.bool_ty
        | _ -> g.int32_ty
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata ->
        let tycon = (tcrefOfAppTy g ty).Deref
        match tycon.GetFieldByName "value__" with 
        | Some rf -> rf.FormalType
        | None -> error(InternalError("no 'value__' field found for enumeration type " + tycon.LogicalName, tycon.Range))

// CLEANUP NOTE: Get rid of this mutation. 
let ClearValReprInfo (f: Val) = 
    f.SetValReprInfo None; f

//--------------------------------------------------------------------------
// Resolve static optimization constraints
//--------------------------------------------------------------------------

let normalizeEnumTy g ty = (if isEnumTy g ty then underlyingTypeOfEnumTy g ty else ty) 

type StaticOptimizationAnswer = 
    | Yes = 1y
    | No = -1y
    | Unknown = 0y

// Most static optimization conditionals in FSharp.Core are
//   ^T : tycon
//
// These decide positively if ^T is nominal and identical to tycon.
// These decide negatively if ^T is nominal and different to tycon.
//
// The "special" static optimization conditionals
//    ^T : ^T 
//    'T : 'T 
// are used as hacks in FSharp.Core as follows:
//    ^T : ^T  --> used in (+), (-) etc. to guard witness-invoking implementations added in F# 5
//    'T : 'T  --> used in FastGenericEqualityComparer, FastGenericComparer to guard struct/tuple implementations 
//
// For performance and compatibility reasons, 'T when 'T is an enum is handled with its own special hack.
// Unlike for other 'T : tycon constraints, 'T can be any enum; it need not (and indeed must not) be identical to System.Enum itself.
//    'T : Enum
//
// In order to add this hack in a backwards-compatible way, we must hide this capability behind a marker type
// which we use solely as an indicator of whether the compiler understands `when 'T : Enum`.
//    'T : SupportsWhenTEnum
//
// canDecideTyparEqn is set to true in IlxGen when the witness-invoking implementation can be used.
let decideStaticOptimizationConstraint g c canDecideTyparEqn = 
    match c with 
    | TTyconEqualsTycon (a, b) when canDecideTyparEqn && typeEquiv g a b && isTyparTy g a ->
        StaticOptimizationAnswer.Yes
    | TTyconEqualsTycon (_, b) when tryTcrefOfAppTy g b |> ValueOption.exists (tyconRefEq g g.SupportsWhenTEnum_tcr) ->
        StaticOptimizationAnswer.Yes
    | TTyconEqualsTycon (a, b) when isEnumTy g a && not (typeEquiv g a g.system_Enum_ty) && typeEquiv g b g.system_Enum_ty ->
        StaticOptimizationAnswer.Yes
    | TTyconEqualsTycon (a, b) ->
        // Both types must be nominal for a definite result
       let rec checkTypes a b =
           let a = normalizeEnumTy g (stripTyEqnsAndMeasureEqns g a)
           match a with
           | AppTy g (tcref1, _) ->
               let b = normalizeEnumTy g (stripTyEqnsAndMeasureEqns g b)
               match b with 
               | AppTy g (tcref2, _) -> 
                if tyconRefEq g tcref1 tcref2 && not (typeEquiv g a g.system_Enum_ty) then StaticOptimizationAnswer.Yes else StaticOptimizationAnswer.No
               | RefTupleTy g _ | FunTy g _ -> StaticOptimizationAnswer.No
               | _ -> StaticOptimizationAnswer.Unknown

           | FunTy g _ ->
               let b = normalizeEnumTy g (stripTyEqnsAndMeasureEqns g b)
               match b with 
               | FunTy g _ -> StaticOptimizationAnswer.Yes
               | AppTy g _ | RefTupleTy g _ -> StaticOptimizationAnswer.No
               | _ -> StaticOptimizationAnswer.Unknown
           | RefTupleTy g ts1 -> 
               let b = normalizeEnumTy g (stripTyEqnsAndMeasureEqns g b)
               match b with 
               | RefTupleTy g ts2 ->
                if ts1.Length = ts2.Length then StaticOptimizationAnswer.Yes
                else StaticOptimizationAnswer.No
               | AppTy g _ | FunTy g _ -> StaticOptimizationAnswer.No
               | _ -> StaticOptimizationAnswer.Unknown
           | _ -> StaticOptimizationAnswer.Unknown
       checkTypes a b
    | TTyconIsStruct a -> 
       let a = normalizeEnumTy g (stripTyEqnsAndMeasureEqns g a)
       match tryTcrefOfAppTy g a with 
       | ValueSome tcref1 -> if tcref1.IsStructOrEnumTycon then StaticOptimizationAnswer.Yes else StaticOptimizationAnswer.No
       | ValueNone -> StaticOptimizationAnswer.Unknown
            
let rec DecideStaticOptimizations g cs canDecideTyparEqn = 
    match cs with 
    | [] -> StaticOptimizationAnswer.Yes
    | h :: t -> 
        let d = decideStaticOptimizationConstraint g h canDecideTyparEqn
        if d = StaticOptimizationAnswer.No then StaticOptimizationAnswer.No 
        elif d = StaticOptimizationAnswer.Yes then DecideStaticOptimizations g t canDecideTyparEqn
        else StaticOptimizationAnswer.Unknown

let mkStaticOptimizationExpr g (cs, e1, e2, m) = 
    let d = DecideStaticOptimizations g cs false
    if d = StaticOptimizationAnswer.No then e2
    elif d = StaticOptimizationAnswer.Yes then e1
    else Expr.StaticOptimization (cs, e1, e2, m)

//--------------------------------------------------------------------------
// Copy expressions, including new names for locally bound values.
// Used to inline expressions.
//--------------------------------------------------------------------------

type ValCopyFlag = 
    | CloneAll
    | CloneAllAndMarkExprValsAsCompilerGenerated
    | OnlyCloneExprVals

// for quotations we do no want to avoid marking values as compiler generated since this may affect the shape of quotation (compiler generated values can be inlined)
let fixValCopyFlagForQuotations = function CloneAllAndMarkExprValsAsCompilerGenerated -> CloneAll | x -> x
    
let markAsCompGen compgen d = 
    let compgen = 
        match compgen with 
        | CloneAllAndMarkExprValsAsCompilerGenerated -> true
        | _ -> false
    { d with val_flags= d.val_flags.WithIsCompilerGenerated(d.val_flags.IsCompilerGenerated || compgen) }

let bindLocalVal (v: Val) (v': Val) tmenv = 
    { tmenv with valRemap=tmenv.valRemap.Add v (mkLocalValRef v') }

let bindLocalVals vs vs' tmenv = 
    { tmenv with valRemap= (vs, vs', tmenv.valRemap) |||> List.foldBack2 (fun v v' acc -> acc.Add v (mkLocalValRef v') ) }

let bindTycons tcs tcs' tyenv =  
    { tyenv with tyconRefRemap= (tcs, tcs', tyenv.tyconRefRemap) |||> List.foldBack2 (fun tc tc' acc -> acc.Add (mkLocalTyconRef tc) (mkLocalTyconRef tc')) }

let remapAttribKind tmenv k =  
    match k with 
    | ILAttrib _ as x -> x
    | FSAttrib vref -> FSAttrib(remapValRef tmenv vref)

let tmenvCopyRemapAndBindTypars remapAttrib tmenv tps = 
    let tps', tyenvinner = copyAndRemapAndBindTyparsFull remapAttrib tmenv tps
    let tmenvinner = tyenvinner 
    tps', tmenvinner

type RemapContext =
    { g: TcGlobals
      stackGuard: StackGuard }

let rec remapAttribImpl ctxt tmenv (Attrib (tcref, kind, args, props, isGetOrSetAttr, targets, m)) = 
    Attrib(
        remapTyconRef tmenv.tyconRefRemap tcref, 
        remapAttribKind tmenv kind, 
        args |> List.map (remapAttribExpr ctxt tmenv), 
        props |> List.map (fun (AttribNamedArg(nm, ty, flg, expr)) -> AttribNamedArg(nm, remapType tmenv ty, flg, remapAttribExpr ctxt tmenv expr)), 
        isGetOrSetAttr, 
        targets, 
        m
    )

and remapAttribExpr ctxt tmenv (AttribExpr(e1, e2)) = 
    AttribExpr(remapExprImpl ctxt CloneAll tmenv e1, remapExprImpl ctxt CloneAll tmenv e2)
    
and remapAttribs ctxt tmenv xs =
    List.map (remapAttribImpl ctxt tmenv) xs

and remapPossibleForallTyImpl ctxt tmenv ty =
    remapTypeFull (remapAttribs ctxt tmenv) tmenv ty

and remapArgData ctxt tmenv (argInfo: ArgReprInfo) : ArgReprInfo =
    { Attribs = remapAttribs ctxt tmenv argInfo.Attribs; Name = argInfo.Name; OtherRange = argInfo.OtherRange }

and remapValReprInfo ctxt tmenv (ValReprInfo(tpNames, arginfosl, retInfo)) =
    ValReprInfo(tpNames, List.mapSquared (remapArgData ctxt tmenv) arginfosl, remapArgData ctxt tmenv retInfo)

and remapValData ctxt tmenv (d: ValData) =
    let ty = d.val_type
    let valReprInfo = d.ValReprInfo
    let tyR = ty |> remapPossibleForallTyImpl ctxt tmenv
    let declaringEntityR = d.TryDeclaringEntity |> remapParentRef tmenv
    let reprInfoR = d.ValReprInfo |> Option.map (remapValReprInfo ctxt tmenv)
    let memberInfoR = d.MemberInfo |> Option.map (remapMemberInfo ctxt d.val_range valReprInfo ty tyR tmenv)
    let attribsR = d.Attribs |> remapAttribs ctxt tmenv
    { d with 
        val_type = tyR
        val_opt_data =
            match d.val_opt_data with
            | Some dd ->
                Some { dd with 
                         val_declaring_entity = declaringEntityR
                         val_repr_info = reprInfoR
                         val_member_info = memberInfoR
                         val_attribs = attribsR }
            | None -> None }

and remapParentRef tyenv p =
    match p with 
    | ParentNone -> ParentNone
    | Parent x -> Parent (x |> remapTyconRef tyenv.tyconRefRemap)

and mapImmediateValsAndTycons ft fv (x: ModuleOrNamespaceType) = 
    let vals = x.AllValsAndMembers |> QueueList.map fv
    let tycons = x.AllEntities |> QueueList.map ft
    ModuleOrNamespaceType(x.ModuleOrNamespaceKind, vals, tycons)

and copyVal compgen (v: Val) = 
    match compgen with 
    | OnlyCloneExprVals when v.IsMemberOrModuleBinding -> v
    | _ -> v |> Construct.NewModifiedVal id

and fixupValData ctxt compgen tmenv (v2: Val) =
    // only fixup if we copy the value
    match compgen with 
    | OnlyCloneExprVals when v2.IsMemberOrModuleBinding -> ()
    | _ ->  
        let newData = remapValData ctxt tmenv v2 |> markAsCompGen compgen
        // uses the same stamp
        v2.SetData newData
    
and copyAndRemapAndBindVals ctxt compgen tmenv vs = 
    let vs2 = vs |> List.map (copyVal compgen)
    let tmenvinner = bindLocalVals vs vs2 tmenv
    vs2 |> List.iter (fixupValData ctxt compgen tmenvinner)
    vs2, tmenvinner

and copyAndRemapAndBindVal ctxt compgen tmenv v = 
    let v2 = v |> copyVal compgen
    let tmenvinner = bindLocalVal v v2 tmenv
    fixupValData ctxt compgen tmenvinner v2
    v2, tmenvinner
    
and remapExprImpl (ctxt: RemapContext) (compgen: ValCopyFlag) (tmenv: Remap) expr =

    // Guard against stack overflow, moving to a whole new stack if necessary
    ctxt.stackGuard.Guard <| fun () ->

    match expr with

    // Handle the linear cases for arbitrary-sized inputs 
    | LinearOpExpr _ 
    | LinearMatchExpr _ 
    | Expr.Sequential _  
    | Expr.Let _ 
    | Expr.DebugPoint _ ->
        remapLinearExpr ctxt compgen tmenv expr id

    // Binding constructs - see also dtrees below 
    | Expr.Lambda (_, ctorThisValOpt, baseValOpt, vs, b, m, bodyTy) -> 
        remapLambaExpr ctxt compgen tmenv (ctorThisValOpt, baseValOpt, vs, b, m, bodyTy)

    | Expr.TyLambda (_, tps, b, m, bodyTy) ->
        let tps', tmenvinner = tmenvCopyRemapAndBindTypars (remapAttribs ctxt tmenv) tmenv tps
        mkTypeLambda m tps' (remapExprImpl ctxt compgen tmenvinner b, remapType tmenvinner bodyTy)

    | Expr.TyChoose (tps, b, m) ->
        let tps', tmenvinner = tmenvCopyRemapAndBindTypars (remapAttribs ctxt tmenv) tmenv tps
        Expr.TyChoose (tps', remapExprImpl ctxt compgen tmenvinner b, m)

    | Expr.LetRec (binds, e, m, _) ->  
        let binds', tmenvinner = copyAndRemapAndBindBindings ctxt compgen tmenv binds 
        Expr.LetRec (binds', remapExprImpl ctxt compgen tmenvinner e, m, Construct.NewFreeVarsCache())

    | Expr.Match (spBind, mExpr, pt, targets, m, ty) ->
        primMkMatch (spBind, mExpr, remapDecisionTree ctxt compgen tmenv pt, 
                     targets |> Array.map (remapTarget ctxt compgen tmenv), 
                     m, remapType tmenv ty)

    | Expr.Val (vr, vf, m) -> 
        let vr' = remapValRef tmenv vr 
        let vf' = remapValFlags tmenv vf
        if vr === vr' && vf === vf' then expr 
        else Expr.Val (vr', vf', m)

    | Expr.Quote (a, dataCell, isFromQueryExpression, m, ty) ->  
        remapQuoteExpr ctxt compgen tmenv (a, dataCell, isFromQueryExpression, m, ty)

    | Expr.Obj (_, ty, basev, basecall, overrides, iimpls, m) -> 
        let basev', tmenvinner = Option.mapFold (copyAndRemapAndBindVal ctxt compgen) tmenv basev 
        mkObjExpr (remapType tmenv ty, basev', 
                   remapExprImpl ctxt compgen tmenv basecall, 
                   List.map (remapMethod ctxt compgen tmenvinner) overrides, 
                   List.map (remapInterfaceImpl ctxt compgen tmenvinner) iimpls, m) 

    // Addresses of immutable field may "leak" across assembly boundaries - see CanTakeAddressOfRecdFieldRef below.
    // This is "ok", in the sense that it is always valid to fix these up to be uses
    // of a temporary local, e.g.
    //       &(E.RF) --> let mutable v = E.RF in &v

    | Expr.Op (TOp.ValFieldGetAddr (rfref, readonly), tinst, [arg], m) when 
          not rfref.RecdField.IsMutable && 
          not (entityRefInThisAssembly ctxt.g.compilingFSharpCore rfref.TyconRef) -> 

        let tinst = remapTypes tmenv tinst 
        let arg = remapExprImpl ctxt compgen tmenv arg 
        let tmp, _ = mkMutableCompGenLocal m WellKnownNames.CopyOfStruct (actualTyOfRecdFieldRef rfref tinst)
        mkCompGenLet m tmp (mkRecdFieldGetViaExprAddr (arg, rfref, tinst, m)) (mkValAddr m readonly (mkLocalValRef tmp))

    | Expr.Op (TOp.UnionCaseFieldGetAddr (uref, cidx, readonly), tinst, [arg], m) when 
          not (uref.FieldByIndex(cidx).IsMutable) && 
          not (entityRefInThisAssembly ctxt.g.compilingFSharpCore uref.TyconRef) -> 

        let tinst = remapTypes tmenv tinst 
        let arg = remapExprImpl ctxt compgen tmenv arg 
        let tmp, _ = mkMutableCompGenLocal m WellKnownNames.CopyOfStruct (actualTyOfUnionFieldRef uref cidx tinst)
        mkCompGenLet m tmp (mkUnionCaseFieldGetProvenViaExprAddr (arg, uref, tinst, cidx, m)) (mkValAddr m readonly (mkLocalValRef tmp))

    | Expr.Op (op, tinst, args, m) -> 
        remapOpExpr ctxt compgen tmenv (op, tinst, args, m) expr

    | Expr.App (e1, e1ty, tyargs, args, m) -> 
        remapAppExpr ctxt compgen tmenv (e1, e1ty, tyargs, args, m) expr

    | Expr.Link eref -> 
        remapExprImpl ctxt compgen tmenv eref.Value

    | Expr.StaticOptimization (cs, e2, e3, m) -> 
       // note that type instantiation typically resolve the static constraints here 
       mkStaticOptimizationExpr ctxt.g (List.map (remapConstraint tmenv) cs, remapExprImpl ctxt compgen tmenv e2, remapExprImpl ctxt compgen tmenv e3, m)

    | Expr.Const (c, m, ty) -> 
        let ty' = remapType tmenv ty 
        if ty === ty' then expr else Expr.Const (c, m, ty')

    | Expr.WitnessArg (traitInfo, m) ->
        let traitInfoR = remapTraitInfo tmenv traitInfo
        Expr.WitnessArg (traitInfoR, m)

and remapLambaExpr (ctxt: RemapContext) (compgen: ValCopyFlag) (tmenv: Remap) (ctorThisValOpt, baseValOpt, vs, body, m, bodyTy) =
    let ctorThisValOptR, tmenv = Option.mapFold (copyAndRemapAndBindVal ctxt compgen) tmenv ctorThisValOpt
    let baseValOptR, tmenv = Option.mapFold (copyAndRemapAndBindVal ctxt compgen) tmenv baseValOpt
    let vsR, tmenv = copyAndRemapAndBindVals ctxt compgen tmenv vs
    let bodyR = remapExprImpl ctxt compgen tmenv body
    let bodyTyR = remapType tmenv bodyTy
    Expr.Lambda (newUnique(), ctorThisValOptR, baseValOptR, vsR, bodyR, m, bodyTyR)

and remapQuoteExpr (ctxt: RemapContext) (compgen: ValCopyFlag) (tmenv: Remap) (a, dataCell, isFromQueryExpression, m, ty) =
    let doData (typeDefs, argTypes, argExprs, res) = (typeDefs, remapTypesAux tmenv argTypes, remapExprs ctxt compgen tmenv argExprs, res)
    let data' =
        match dataCell.Value with 
        | None -> None
        | Some (data1, data2) -> Some (doData data1, doData data2)
        // fix value of compgen for both original expression and pickled AST
    let compgen = fixValCopyFlagForQuotations compgen
    Expr.Quote (remapExprImpl ctxt compgen tmenv a, ref data', isFromQueryExpression, m, remapType tmenv ty)

and remapOpExpr (ctxt: RemapContext) (compgen: ValCopyFlag) (tmenv: Remap) (op, tinst, args, m) origExpr =
    let opR = remapOp tmenv op 
    let tinstR = remapTypes tmenv tinst 
    let argsR = remapExprs ctxt compgen tmenv args 
    if op === opR && tinst === tinstR && args === argsR then origExpr 
    else Expr.Op (opR, tinstR, argsR, m)

and remapAppExpr (ctxt: RemapContext) (compgen: ValCopyFlag) (tmenv: Remap) (e1, e1ty, tyargs, args, m) origExpr =
    let e1R = remapExprImpl ctxt compgen tmenv e1 
    let e1tyR = remapPossibleForallTyImpl ctxt tmenv e1ty 
    let tyargsR = remapTypes tmenv tyargs 
    let argsR = remapExprs ctxt compgen tmenv args 
    if e1 === e1R && e1ty === e1tyR && tyargs === tyargsR && args === argsR then origExpr 
    else Expr.App (e1R, e1tyR, tyargsR, argsR, m)

and remapTarget ctxt compgen tmenv (TTarget(vs, e, flags)) = 
    let vsR, tmenvinner = copyAndRemapAndBindVals ctxt compgen tmenv vs 
    TTarget(vsR, remapExprImpl ctxt compgen tmenvinner e, flags)

and remapLinearExpr ctxt compgen tmenv expr contf =

    match expr with 

    | Expr.Let (bind, bodyExpr, m, _) ->  
        let bindR, tmenvinner = copyAndRemapAndBindBinding ctxt compgen tmenv bind
        // tailcall for the linear position
        remapLinearExpr ctxt compgen tmenvinner bodyExpr (contf << mkLetBind m bindR)

    | Expr.Sequential (expr1, expr2, dir, m) -> 
        let expr1R = remapExprImpl ctxt compgen tmenv expr1 
        // tailcall for the linear position
        remapLinearExpr ctxt compgen tmenv expr2 (contf << (fun expr2R -> 
            if expr1 === expr1R && expr2 === expr2R then expr 
            else Expr.Sequential (expr1R, expr2R, dir, m)))

    | LinearMatchExpr (spBind, mExpr, dtree, tg1, expr2, m2, ty) ->
        let dtreeR = remapDecisionTree ctxt compgen tmenv dtree
        let tg1R = remapTarget ctxt compgen tmenv tg1
        let tyR = remapType tmenv ty
        // tailcall for the linear position
        remapLinearExpr ctxt compgen tmenv expr2 (contf << (fun expr2R -> 
            rebuildLinearMatchExpr (spBind, mExpr, dtreeR, tg1R, expr2R, m2, tyR)))

    | LinearOpExpr (op, tyargs, argsFront, argLast, m) -> 
        let opR = remapOp tmenv op 
        let tinstR = remapTypes tmenv tyargs 
        let argsFrontR = remapExprs ctxt compgen tmenv argsFront 
        // tailcall for the linear position
        remapLinearExpr ctxt compgen tmenv argLast (contf << (fun argLastR -> 
            if op === opR && tyargs === tinstR && argsFront === argsFrontR && argLast === argLastR then expr 
            else rebuildLinearOpExpr (opR, tinstR, argsFrontR, argLastR, m)))

    | Expr.DebugPoint (dpm, innerExpr) -> 
        remapLinearExpr ctxt compgen tmenv innerExpr (contf << (fun innerExprR ->
            Expr.DebugPoint (dpm, innerExprR)))

    | _ -> 
        contf (remapExprImpl ctxt compgen tmenv expr) 

and remapConstraint tyenv c = 
    match c with 
    | TTyconEqualsTycon(ty1, ty2) -> TTyconEqualsTycon(remapType tyenv ty1, remapType tyenv ty2)
    | TTyconIsStruct ty1 -> TTyconIsStruct(remapType tyenv ty1)

and remapOp tmenv op = 
    match op with 
    | TOp.Recd (ctor, tcref) -> TOp.Recd (ctor, remapTyconRef tmenv.tyconRefRemap tcref)
    | TOp.UnionCaseTagGet tcref -> TOp.UnionCaseTagGet (remapTyconRef tmenv.tyconRefRemap tcref)
    | TOp.UnionCase ucref -> TOp.UnionCase (remapUnionCaseRef tmenv.tyconRefRemap ucref)
    | TOp.UnionCaseProof ucref -> TOp.UnionCaseProof (remapUnionCaseRef tmenv.tyconRefRemap ucref)
    | TOp.ExnConstr ec -> TOp.ExnConstr (remapTyconRef tmenv.tyconRefRemap ec)
    | TOp.ExnFieldGet (ec, n) -> TOp.ExnFieldGet (remapTyconRef tmenv.tyconRefRemap ec, n)
    | TOp.ExnFieldSet (ec, n) -> TOp.ExnFieldSet (remapTyconRef tmenv.tyconRefRemap ec, n)
    | TOp.ValFieldSet rfref -> TOp.ValFieldSet (remapRecdFieldRef tmenv.tyconRefRemap rfref)
    | TOp.ValFieldGet rfref -> TOp.ValFieldGet (remapRecdFieldRef tmenv.tyconRefRemap rfref)
    | TOp.ValFieldGetAddr (rfref, readonly) -> TOp.ValFieldGetAddr (remapRecdFieldRef tmenv.tyconRefRemap rfref, readonly)
    | TOp.UnionCaseFieldGet (ucref, n) -> TOp.UnionCaseFieldGet (remapUnionCaseRef tmenv.tyconRefRemap ucref, n)
    | TOp.UnionCaseFieldGetAddr (ucref, n, readonly) -> TOp.UnionCaseFieldGetAddr (remapUnionCaseRef tmenv.tyconRefRemap ucref, n, readonly)
    | TOp.UnionCaseFieldSet (ucref, n) -> TOp.UnionCaseFieldSet (remapUnionCaseRef tmenv.tyconRefRemap ucref, n)
    | TOp.ILAsm (instrs, retTypes) -> 
        let retTypes2 = remapTypes tmenv retTypes
        if retTypes === retTypes2 then op else
        TOp.ILAsm (instrs, retTypes2)
    | TOp.TraitCall traitInfo -> TOp.TraitCall (remapTraitInfo tmenv traitInfo)
    | TOp.LValueOp (kind, lvr) -> TOp.LValueOp (kind, remapValRef tmenv lvr)
    | TOp.ILCall (isVirtual, isProtected, isStruct, isCtor, valUseFlag, isProperty, noTailCall, ilMethRef, enclTypeInst, methInst, retTypes) -> 
       TOp.ILCall (isVirtual, isProtected, isStruct, isCtor, remapValFlags tmenv valUseFlag, 
                   isProperty, noTailCall, ilMethRef, remapTypes tmenv enclTypeInst, 
                   remapTypes tmenv methInst, remapTypes tmenv retTypes)
    | _ -> op
    
and remapValFlags tmenv x =
    match x with 
    | PossibleConstrainedCall ty -> PossibleConstrainedCall (remapType tmenv ty)
    | _ -> x

and remapExprs ctxt compgen tmenv es = List.mapq (remapExprImpl ctxt compgen tmenv) es

and remapFlatExprs ctxt compgen tmenv es = List.mapq (remapExprImpl ctxt compgen tmenv) es

and remapDecisionTree ctxt compgen tmenv x =
    match x with 
    | TDSwitch(e1, cases, dflt, m) -> 
        let e1R = remapExprImpl ctxt compgen tmenv e1
        let casesR =
            cases |> List.map (fun (TCase(test, subTree)) -> 
                let testR = 
                    match test with 
                    | DecisionTreeTest.UnionCase (uc, tinst) -> DecisionTreeTest.UnionCase(remapUnionCaseRef tmenv.tyconRefRemap uc, remapTypes tmenv tinst)
                    | DecisionTreeTest.ArrayLength (n, ty) -> DecisionTreeTest.ArrayLength(n, remapType tmenv ty)
                    | DecisionTreeTest.Const _ -> test
                    | DecisionTreeTest.IsInst (srcTy, tgtTy) -> DecisionTreeTest.IsInst (remapType tmenv srcTy, remapType tmenv tgtTy) 
                    | DecisionTreeTest.IsNull -> DecisionTreeTest.IsNull 
                    | DecisionTreeTest.ActivePatternCase _ -> failwith "DecisionTreeTest.ActivePatternCase should only be used during pattern match compilation"
                    | DecisionTreeTest.Error(m) -> DecisionTreeTest.Error(m)
                let subTreeR = remapDecisionTree ctxt compgen tmenv subTree
                TCase(testR, subTreeR))
        let dfltR = Option.map (remapDecisionTree ctxt compgen tmenv) dflt
        TDSwitch(e1R, casesR, dfltR, m)

    | TDSuccess (es, n) -> 
        TDSuccess (remapFlatExprs ctxt compgen tmenv es, n)

    | TDBind (bind, rest) -> 
        let bindR, tmenvinner = copyAndRemapAndBindBinding ctxt compgen tmenv bind
        TDBind (bindR, remapDecisionTree ctxt compgen tmenvinner rest)
        
and copyAndRemapAndBindBinding ctxt compgen tmenv (bind: Binding) =
    let v = bind.Var
    let vR, tmenv = copyAndRemapAndBindVal ctxt compgen tmenv v
    remapAndRenameBind ctxt compgen tmenv bind vR, tmenv

and copyAndRemapAndBindBindings ctxt compgen tmenv binds = 
    let vsR, tmenvinner = copyAndRemapAndBindVals ctxt compgen tmenv (valsOfBinds binds)
    remapAndRenameBinds ctxt compgen tmenvinner binds vsR, tmenvinner

and remapAndRenameBinds ctxt compgen tmenvinner binds vsR =
    List.map2 (remapAndRenameBind ctxt compgen tmenvinner) binds vsR

and remapAndRenameBind ctxt compgen tmenvinner (TBind(_, repr, letSeqPtOpt)) vR =
    TBind(vR, remapExprImpl ctxt compgen tmenvinner repr, letSeqPtOpt)

and remapMethod ctxt compgen tmenv (TObjExprMethod(slotsig, attribs, tps, vs, e, m)) =
    let attribs2 = attribs |> remapAttribs ctxt tmenv
    let slotsig2 = remapSlotSig (remapAttribs ctxt tmenv) tmenv slotsig
    let tps2, tmenvinner = tmenvCopyRemapAndBindTypars (remapAttribs ctxt tmenv) tmenv tps
    let vs2, tmenvinner2 = List.mapFold (copyAndRemapAndBindVals ctxt compgen) tmenvinner vs
    let e2 = remapExprImpl ctxt compgen tmenvinner2 e
    TObjExprMethod(slotsig2, attribs2, tps2, vs2, e2, m)

and remapInterfaceImpl ctxt compgen tmenv (ty, overrides) =
    (remapType tmenv ty, List.map (remapMethod ctxt compgen tmenv) overrides)

and remapRecdField ctxt tmenv x = 
    { x with 
          rfield_type = x.rfield_type |> remapPossibleForallTyImpl ctxt tmenv
          rfield_pattribs = x.rfield_pattribs |> remapAttribs ctxt tmenv
          rfield_fattribs = x.rfield_fattribs |> remapAttribs ctxt tmenv } 

and remapRecdFields ctxt tmenv (x: TyconRecdFields) =
    x.AllFieldsAsList |> List.map (remapRecdField ctxt tmenv) |> Construct.MakeRecdFieldsTable 

and remapUnionCase ctxt tmenv (x: UnionCase) = 
    { x with 
          FieldTable = x.FieldTable |> remapRecdFields ctxt tmenv
          ReturnType = x.ReturnType |> remapType tmenv
          Attribs = x.Attribs |> remapAttribs ctxt tmenv } 

and remapUnionCases ctxt tmenv (x: TyconUnionData) =
    x.UnionCasesAsList |> List.map (remapUnionCase ctxt tmenv) |> Construct.MakeUnionCases 

and remapFsObjData ctxt tmenv x = 
    { 
        fsobjmodel_cases = remapUnionCases ctxt tmenv x.fsobjmodel_cases
        fsobjmodel_kind = 
            (match x.fsobjmodel_kind with 
            | TFSharpDelegate slotsig -> TFSharpDelegate (remapSlotSig (remapAttribs ctxt tmenv) tmenv slotsig)
            | _ -> x.fsobjmodel_kind)
        fsobjmodel_vslots = x.fsobjmodel_vslots |> List.map (remapValRef tmenv)
        fsobjmodel_rfields = x.fsobjmodel_rfields |> remapRecdFields ctxt tmenv } 

and remapTyconRepr ctxt tmenv repr = 
    match repr with 
    | TFSharpTyconRepr x -> TFSharpTyconRepr (remapFsObjData ctxt tmenv x)
    | TILObjectRepr _ -> failwith "cannot remap IL type definitions"
#if !NO_TYPEPROVIDERS
    | TProvidedNamespaceRepr _ -> repr
    | TProvidedTypeRepr info -> 
       TProvidedTypeRepr 
            { info with 
                 LazyBaseType = info.LazyBaseType.Force (range0, ctxt.g.obj_ty_withNulls) |> remapType tmenv |> LazyWithContext.NotLazy
                 // The load context for the provided type contains TyconRef objects. We must remap these.
                 // This is actually done on-demand (see the implementation of ProvidedTypeContext)
                 ProvidedType = 
                     info.ProvidedType.PApplyNoFailure (fun st -> 
                         let ctxt = st.Context.RemapTyconRefs(unbox >> remapTyconRef tmenv.tyconRefRemap >> box >> (!!)) 
                         ProvidedType.ApplyContext (st, ctxt)) }
#endif
    | TNoRepr -> repr
    | TAsmRepr _ -> repr
    | TMeasureableRepr x -> TMeasureableRepr (remapType tmenv x)

and remapTyconAug tmenv (x: TyconAugmentation) = 
    { x with 
          tcaug_equals = x.tcaug_equals |> Option.map (mapPair (remapValRef tmenv, remapValRef tmenv))
          tcaug_compare = x.tcaug_compare |> Option.map (mapPair (remapValRef tmenv, remapValRef tmenv))
          tcaug_compare_withc = x.tcaug_compare_withc |> Option.map(remapValRef tmenv)
          tcaug_hash_and_equals_withc = x.tcaug_hash_and_equals_withc |> Option.map (mapQuadruple (remapValRef tmenv, remapValRef tmenv, remapValRef tmenv, Option.map (remapValRef tmenv)))
          tcaug_adhoc = x.tcaug_adhoc |> NameMap.map (List.map (remapValRef tmenv))
          tcaug_adhoc_list = x.tcaug_adhoc_list |> ResizeArray.map (fun (flag, vref) -> (flag, remapValRef tmenv vref))
          tcaug_super = x.tcaug_super |> Option.map (remapType tmenv)
          tcaug_interfaces = x.tcaug_interfaces |> List.map (map1Of3 (remapType tmenv)) } 

and remapTyconExnInfo ctxt tmenv inp =
    match inp with 
    | TExnAbbrevRepr x -> TExnAbbrevRepr (remapTyconRef tmenv.tyconRefRemap x)
    | TExnFresh x -> TExnFresh (remapRecdFields ctxt tmenv x)
    | TExnAsmRepr _ | TExnNone -> inp 

and remapMemberInfo ctxt m valReprInfo ty tyR tmenv x = 
    // The slotsig in the ImplementedSlotSigs is w.r.t. the type variables in the value's type. 
    // REVIEW: this is a bit gross. It would be nice if the slotsig was standalone 
    assert (Option.isSome valReprInfo)
    let tpsorig, _, _, _ = GetMemberTypeInFSharpForm ctxt.g x.MemberFlags (Option.get valReprInfo) ty m
    let tps, _, _, _ = GetMemberTypeInFSharpForm ctxt.g x.MemberFlags (Option.get valReprInfo) tyR m
    let renaming, _ = mkTyparToTyparRenaming tpsorig tps 
    let tmenv = { tmenv with tpinst = tmenv.tpinst @ renaming } 
    { x with 
        ApparentEnclosingEntity = x.ApparentEnclosingEntity |> remapTyconRef tmenv.tyconRefRemap 
        ImplementedSlotSigs = x.ImplementedSlotSigs |> List.map (remapSlotSig (remapAttribs ctxt tmenv) tmenv)
    } 

and copyAndRemapAndBindModTy ctxt compgen tmenv mty = 
    let tycons = allEntitiesOfModuleOrNamespaceTy mty
    let vs = allValsOfModuleOrNamespaceTy mty
    let _, _, tmenvinner = copyAndRemapAndBindTyconsAndVals ctxt compgen tmenv tycons vs
    (mapImmediateValsAndTycons (renameTycon tmenvinner) (renameVal tmenvinner) mty), tmenvinner

and renameTycon tyenv x = 
    let tcref = 
        try
            let res = tyenv.tyconRefRemap[mkLocalTyconRef x]
            res
        with :? KeyNotFoundException -> 
            errorR(InternalError("couldn't remap internal tycon " + showL(DebugPrint.tyconL x), x.Range))
            mkLocalTyconRef x 
    tcref.Deref

and renameVal tmenv x = 
    match tmenv.valRemap.TryFind x with 
    | Some v -> v.Deref
    | None -> x

and copyTycon compgen (tycon: Tycon) = 
    match compgen with 
    | OnlyCloneExprVals -> tycon
    | _ -> Construct.NewClonedTycon tycon

/// This operates over a whole nested collection of tycons and vals simultaneously *)
and copyAndRemapAndBindTyconsAndVals ctxt compgen tmenv tycons vs = 
    let tyconsR = tycons |> List.map (copyTycon compgen)

    let tmenvinner = bindTycons tycons tyconsR tmenv
    
    // Values need to be copied and renamed. 
    let vsR, tmenvinner = copyAndRemapAndBindVals ctxt compgen tmenvinner vs

    // "if a type constructor is hidden then all its inner values and inner type constructors must also be hidden" 
    // Hence we can just lookup the inner tycon/value mappings in the tables. 

    let lookupVal (v: Val) = 
        let vref = 
            try  
               let res = tmenvinner.valRemap[v]
               res 
            with :? KeyNotFoundException -> 
                errorR(InternalError(sprintf "couldn't remap internal value '%s'" v.LogicalName, v.Range))
                mkLocalValRef v
        vref.Deref
        
    let lookupTycon tycon = 
        let tcref = 
            try 
                let res = tmenvinner.tyconRefRemap[mkLocalTyconRef tycon]
                res
            with :? KeyNotFoundException -> 
                errorR(InternalError("couldn't remap internal tycon " + showL(DebugPrint.tyconL tycon), tycon.Range))
                mkLocalTyconRef tycon
        tcref.Deref

    (tycons, tyconsR) ||> List.iter2 (fun tcd tcdR ->
        let lookupTycon tycon = lookupTycon tycon
        let tpsR, tmenvinner2 = tmenvCopyRemapAndBindTypars (remapAttribs ctxt tmenvinner) tmenvinner (tcd.entity_typars.Force(tcd.entity_range))
        tcdR.entity_typars <- LazyWithContext.NotLazy tpsR
        tcdR.entity_attribs <- tcd.entity_attribs |> remapAttribs ctxt tmenvinner2
        tcdR.entity_tycon_repr <- tcd.entity_tycon_repr |> remapTyconRepr ctxt tmenvinner2
        let typeAbbrevR = tcd.TypeAbbrev |> Option.map (remapType tmenvinner2)
        tcdR.entity_tycon_tcaug <- tcd.entity_tycon_tcaug |> remapTyconAug tmenvinner2
        tcdR.entity_modul_type <- MaybeLazy.Strict (tcd.entity_modul_type.Value 
                                                        |> mapImmediateValsAndTycons lookupTycon lookupVal)
        let exnInfoR = tcd.ExceptionInfo |> remapTyconExnInfo ctxt tmenvinner2
        match tcdR.entity_opt_data with
        | Some optData -> tcdR.entity_opt_data <- Some { optData with entity_tycon_abbrev = typeAbbrevR; entity_exn_info = exnInfoR }
        | _ -> 
            tcdR.SetTypeAbbrev typeAbbrevR
            tcdR.SetExceptionInfo exnInfoR)
    tyconsR, vsR, tmenvinner


and allTyconsOfTycon (tycon: Tycon) =
    seq { yield tycon
          for nestedTycon in tycon.ModuleOrNamespaceType.AllEntities do
              yield! allTyconsOfTycon nestedTycon }

and allEntitiesOfModDef mdef =
    seq { match mdef with 
          | TMDefRec(_, _, tycons, mbinds, _) -> 
              for tycon in tycons do 
                  yield! allTyconsOfTycon tycon
              for mbind in mbinds do 
                match mbind with 
                | ModuleOrNamespaceBinding.Binding _ -> ()
                | ModuleOrNamespaceBinding.Module(mspec, def) -> 
                  yield mspec
                  yield! allEntitiesOfModDef def
          | TMDefLet _ -> ()
          | TMDefDo _ -> ()
          | TMDefOpens _ -> ()
          | TMDefs defs -> 
              for def in defs do 
                  yield! allEntitiesOfModDef def
    }

and allValsOfModDef mdef = 
    seq { match mdef with 
          | TMDefRec(_, _, tycons, mbinds, _) -> 
              yield! abstractSlotValsOfTycons tycons 
              for mbind in mbinds do 
                match mbind with 
                | ModuleOrNamespaceBinding.Binding bind -> yield bind.Var
                | ModuleOrNamespaceBinding.Module(_, def) -> yield! allValsOfModDef def
          | TMDefLet(bind, _) -> 
              yield bind.Var
          | TMDefDo _ -> ()
          | TMDefOpens _ -> ()
          | TMDefs defs -> 
              for def in defs do 
                  yield! allValsOfModDef def
    }

and copyAndRemapModDef ctxt compgen tmenv mdef =
    let tycons = allEntitiesOfModDef mdef |> List.ofSeq
    let vs = allValsOfModDef mdef |> List.ofSeq
    let _, _, tmenvinner = copyAndRemapAndBindTyconsAndVals ctxt compgen tmenv tycons vs
    remapAndRenameModDef ctxt compgen tmenvinner mdef

and remapAndRenameModDefs ctxt compgen tmenv x = 
    List.map (remapAndRenameModDef ctxt compgen tmenv) x 

and remapOpenDeclarations tmenv opens =
    opens |> List.map (fun od -> 
        { od with 
                Modules = od.Modules |> List.map (remapTyconRef tmenv.tyconRefRemap)
                Types = od.Types |> List.map (remapType tmenv)
        })

and remapAndRenameModDef ctxt compgen tmenv mdef =
    match mdef with 
    | TMDefRec(isRec, opens, tycons, mbinds, m) -> 
        // Abstract (virtual) vslots in the tycons at TMDefRec nodes are binders. They also need to be copied and renamed. 
        let opensR = remapOpenDeclarations tmenv opens
        let tyconsR = tycons |> List.map (renameTycon tmenv)
        let mbindsR = mbinds |> List.map (remapAndRenameModBind ctxt compgen tmenv)
        TMDefRec(isRec, opensR, tyconsR, mbindsR, m)
    | TMDefLet(bind, m) ->
        let v = bind.Var
        let bind = remapAndRenameBind ctxt compgen tmenv bind (renameVal tmenv v)
        TMDefLet(bind, m)
    | TMDefDo(e, m) ->
        let e = remapExprImpl ctxt compgen tmenv e
        TMDefDo(e, m)
    | TMDefOpens opens ->
        let opens = remapOpenDeclarations tmenv opens
        TMDefOpens opens
    | TMDefs defs -> 
        let defs = remapAndRenameModDefs ctxt compgen tmenv defs
        TMDefs defs

and remapAndRenameModBind ctxt compgen tmenv x = 
    match x with 
    | ModuleOrNamespaceBinding.Binding bind -> 
        let v2 = bind |> valOfBind |> renameVal tmenv
        let bind2 = remapAndRenameBind ctxt compgen tmenv bind v2
        ModuleOrNamespaceBinding.Binding bind2
    | ModuleOrNamespaceBinding.Module(mspec, def) ->
        let mspec = renameTycon tmenv mspec
        let def = remapAndRenameModDef ctxt compgen tmenv def
        ModuleOrNamespaceBinding.Module(mspec, def)

and remapImplFile ctxt compgen tmenv implFile = 
    let (CheckedImplFile (fragName, signature, contents, hasExplicitEntryPoint, isScript, anonRecdTypes, namedDebugPointsForInlinedCode)) = implFile
    let contentsR = copyAndRemapModDef ctxt compgen tmenv contents
    let signatureR, tmenv = copyAndRemapAndBindModTy ctxt compgen tmenv signature
    let implFileR = CheckedImplFile (fragName, signatureR, contentsR, hasExplicitEntryPoint, isScript, anonRecdTypes, namedDebugPointsForInlinedCode)
    implFileR, tmenv

// Entry points

let remapAttrib g tmenv attrib = 
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapAttribImpl ctxt tmenv attrib

let remapExpr g (compgen: ValCopyFlag) (tmenv: Remap) expr =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapExprImpl ctxt compgen tmenv expr

let remapPossibleForallTy g tmenv ty =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapPossibleForallTyImpl ctxt tmenv ty

let copyModuleOrNamespaceType g compgen mtyp =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    copyAndRemapAndBindModTy ctxt compgen Remap.Empty mtyp |> fst

let copyExpr g compgen e =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapExprImpl ctxt compgen Remap.Empty e    

let copyImplFile g compgen e =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapImplFile ctxt compgen Remap.Empty e |> fst

let instExpr g tpinst e =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapExprImpl ctxt CloneAll (mkInstRemap tpinst) e

//--------------------------------------------------------------------------
// Replace Marks - adjust debugging marks when a lambda gets
// eliminated (i.e. an expression gets inlined)
//--------------------------------------------------------------------------

let rec remarkExpr (m: range) x =
    match x with
    | Expr.Lambda (uniq, ctorThisValOpt, baseValOpt, vs, b, _, bodyTy) ->
        Expr.Lambda (uniq, ctorThisValOpt, baseValOpt, vs, remarkExpr m b, m, bodyTy)  

    | Expr.TyLambda (uniq, tps, b, _, bodyTy) ->
        Expr.TyLambda (uniq, tps, remarkExpr m b, m, bodyTy)

    | Expr.TyChoose (tps, b, _) ->
        Expr.TyChoose (tps, remarkExpr m b, m)

    | Expr.LetRec (binds, e, _, fvs) ->
        Expr.LetRec (remarkBinds m binds, remarkExpr m e, m, fvs)

    | Expr.Let (bind, e, _, fvs) ->
        Expr.Let (remarkBind m bind, remarkExpr m e, m, fvs)

    | Expr.Match (_, _, pt, targets, _, ty) ->
        let targetsR = targets |> Array.map (fun (TTarget(vs, e, flags)) -> TTarget(vs, remarkExpr m e, flags))
        primMkMatch (DebugPointAtBinding.NoneAtInvisible, m, remarkDecisionTree m pt, targetsR, m, ty)

    | Expr.Val (x, valUseFlags, _) ->
        Expr.Val (x, valUseFlags, m)

    | Expr.Quote (a, conv, isFromQueryExpression, _, ty) ->
        Expr.Quote (remarkExpr m a, conv, isFromQueryExpression, m, ty)

    | Expr.Obj (n, ty, basev, basecall, overrides, iimpls, _) -> 
        Expr.Obj (n, ty, basev, remarkExpr m basecall, 
                  List.map (remarkObjExprMethod m) overrides, 
                  List.map (remarkInterfaceImpl m) iimpls, m)

    | Expr.Op (op, tinst, args, _) -> 

        // This code allows a feature where if a 'while'/'for' etc in a computation expression is
        // implemented using code inlining and is ultimately implemented by a corresponding construct somewhere
        // in the remark'd code then at least one debug point is recovered, based on the noted debug point for the original construct.
        //
        // However it is imperfect, since only one debug point is recovered
        let op = 
            match op with 
            | TOp.IntegerForLoop (_, _, style) -> TOp.IntegerForLoop(DebugPointAtFor.No, DebugPointAtInOrTo.No, style)
            | TOp.While (_, marker) -> TOp.While(DebugPointAtWhile.No, marker)
            | TOp.TryFinally _ -> TOp.TryFinally (DebugPointAtTry.No, DebugPointAtFinally.No)
            | TOp.TryWith _ -> TOp.TryWith (DebugPointAtTry.No, DebugPointAtWith.No)
            | _ -> op
        Expr.Op (op, tinst, remarkExprs m args, m)

    | Expr.Link eref -> 
        // Preserve identity of fixup nodes during remarkExpr
        eref.Value <- remarkExpr m eref.Value
        x

    | Expr.App (e1, e1ty, tyargs, args, _) ->
        Expr.App (remarkExpr m e1, e1ty, tyargs, remarkExprs m args, m)

    | Expr.Sequential (e1, e2, dir, _) ->
        let e1R = remarkExpr m e1
        let e2R = remarkExpr m e2
        Expr.Sequential (e1R, e2R, dir, m)

    | Expr.StaticOptimization (eqns, e2, e3, _) ->
        Expr.StaticOptimization (eqns, remarkExpr m e2, remarkExpr m e3, m)

    | Expr.Const (c, _, ty) ->
        Expr.Const (c, m, ty)
  
    | Expr.WitnessArg (witnessInfo, _) ->
        Expr.WitnessArg (witnessInfo, m)

    | Expr.DebugPoint (_, innerExpr) ->
        remarkExpr m innerExpr

and remarkObjExprMethod m (TObjExprMethod(slotsig, attribs, tps, vs, e, _)) = 
    TObjExprMethod(slotsig, attribs, tps, vs, remarkExpr m e, m)

and remarkInterfaceImpl m (ty, overrides) = 
    (ty, List.map (remarkObjExprMethod m) overrides)

and remarkExprs m es = es |> List.map (remarkExpr m) 

and remarkDecisionTree m x =
    match x with 
    | TDSwitch(e1, cases, dflt, _) ->
        let e1R = remarkExpr m e1
        let casesR = cases |> List.map (fun (TCase(test, y)) -> TCase(test, remarkDecisionTree m y))
        let dfltR = Option.map (remarkDecisionTree m) dflt
        TDSwitch(e1R, casesR, dfltR, m)
    | TDSuccess (es, n) ->
        TDSuccess (remarkExprs m es, n)
    | TDBind (bind, rest) ->
        TDBind(remarkBind m bind, remarkDecisionTree m rest)

and remarkBinds m binds = List.map (remarkBind m) binds

// This very deliberately drops the sequence points since this is used when adjusting the marks for inlined expressions 
and remarkBind m (TBind(v, repr, _)) = 
    TBind(v, remarkExpr m repr, DebugPointAtBinding.NoneAtSticky)

//--------------------------------------------------------------------------
// Mutability analysis
//--------------------------------------------------------------------------

let isRecdOrStructFieldDefinitelyMutable (f: RecdField) = not f.IsStatic && f.IsMutable

let isUnionCaseDefinitelyMutable (uc: UnionCase) = uc.FieldTable.FieldsByIndex |> Array.exists isRecdOrStructFieldDefinitelyMutable

let isUnionCaseRefDefinitelyMutable (uc: UnionCaseRef) = uc.UnionCase |> isUnionCaseDefinitelyMutable
  
/// This is an incomplete check for .NET struct types. Returning 'false' doesn't mean the thing is immutable.
let isRecdOrUnionOrStructTyconRefDefinitelyMutable (tcref: TyconRef) = 
    let tycon = tcref.Deref
    if tycon.IsUnionTycon then 
        tycon.UnionCasesArray |> Array.exists isUnionCaseDefinitelyMutable
    elif tycon.IsRecordTycon || tycon.IsStructOrEnumTycon then 
        // Note: This only looks at the F# fields, causing oddities.
        // See https://github.com/dotnet/fsharp/pull/4576
        tycon.AllFieldsArray |> Array.exists isRecdOrStructFieldDefinitelyMutable
    else
        false
  
// Although from the pure F# perspective exception values cannot be changed, the .NET 
// implementation of exception objects attaches a whole bunch of stack information to 
// each raised object. Hence we treat exception objects as if they have identity 
let isExnDefinitelyMutable (_ecref: TyconRef) = true 

// Some of the implementations of library functions on lists use mutation on the tail 
// of the cons cell. These cells are always private, i.e. not accessible by any other 
// code until the construction of the entire return list has been completed. 
// However, within the implementation code reads of the tail cell must in theory be treated 
// with caution. Hence we are conservative and within FSharp.Core we don't treat list 
// reads as if they were pure. 
let isUnionCaseFieldMutable (g: TcGlobals) (ucref: UnionCaseRef) n = 
    (g.compilingFSharpCore && tyconRefEq g ucref.TyconRef g.list_tcr_canon && n = 1) ||
    (ucref.FieldByIndex n).IsMutable
  
let isExnFieldMutable ecref n = 
    if n < 0 || n >= List.length (recdFieldsOfExnDefRef ecref) then errorR(InternalError(sprintf "isExnFieldMutable, exnc = %s, n = %d" ecref.LogicalName n, ecref.Range))
    (recdFieldOfExnDefRefByIdx ecref n).IsMutable

let useGenuineField (tycon: Tycon) (f: RecdField) = 
    Option.isSome f.LiteralValue || tycon.IsEnumTycon || f.rfield_secret || (not f.IsStatic && f.rfield_mutable && not tycon.IsRecordTycon)

let ComputeFieldName tycon f = 
    if useGenuineField tycon f then f.rfield_id.idText
    else CompilerGeneratedName f.rfield_id.idText 

//-------------------------------------------------------------------------
// Helpers for building code contained in the initial environment
//------------------------------------------------------------------------- 

let isQuotedExprTy g ty = match tryAppTy g ty with ValueSome (tcref, _) -> tyconRefEq g tcref g.expr_tcr | _ -> false

let destQuotedExprTy g ty = match tryAppTy g ty with ValueSome (_, [ty]) -> ty | _ -> failwith "destQuotedExprTy"

let mkQuotedExprTy (g: TcGlobals) ty = TType_app(g.expr_tcr, [ty], g.knownWithoutNull)

let mkRawQuotedExprTy (g: TcGlobals) = TType_app(g.raw_expr_tcr, [], g.knownWithoutNull)

let mkAnyTupledTy (g: TcGlobals) tupInfo tys = 
    match tys with 
    | [] -> g.unit_ty 
    | [h] -> h
    | _ -> TType_tuple(tupInfo, tys)

let mkAnyAnonRecdTy (_g: TcGlobals) anonInfo tys = 
    TType_anon(anonInfo, tys)

let mkRefTupledTy g tys = mkAnyTupledTy g tupInfoRef tys

let mkRefTupledVarsTy g vs = mkRefTupledTy g (typesOfVals vs)

let mkMethodTy g argTys retTy = mkIteratedFunTy g (List.map (mkRefTupledTy g) argTys) retTy 

let mkArrayType (g: TcGlobals) ty = TType_app (g.array_tcr_nice, [ty], g.knownWithoutNull)

let mkByteArrayTy (g: TcGlobals) = mkArrayType g g.byte_ty

//---------------------------------------------------------------------------
// Witnesses
//---------------------------------------------------------------------------

let GenWitnessArgTys (g: TcGlobals) (traitInfo: TraitWitnessInfo) =
    let (TraitWitnessInfo(_tys, _nm, _memFlags, argTys, _rty)) = traitInfo
    let argTys = if argTys.IsEmpty then [g.unit_ty] else argTys
    let argTysl = List.map List.singleton argTys
    argTysl

let GenWitnessTy (g: TcGlobals) (traitInfo: TraitWitnessInfo) =
    let retTy = match traitInfo.ReturnType with None -> g.unit_ty | Some ty -> ty
    let argTysl = GenWitnessArgTys g traitInfo
    mkMethodTy g argTysl retTy 

let GenWitnessTys (g: TcGlobals) (cxs: TraitWitnessInfos) =
    if g.generateWitnesses then 
        cxs |> List.map (GenWitnessTy g)
    else
        []

//--------------------------------------------------------------------------
// tyOfExpr
//--------------------------------------------------------------------------
 
let rec tyOfExpr g expr = 
    match expr with 
    | Expr.App (_, fty, tyargs, args, _) -> applyTys g fty (tyargs, args)
    | Expr.Obj (_, ty, _, _, _, _, _)  
    | Expr.Match (_, _, _, _, _, ty) 
    | Expr.Quote (_, _, _, _, ty) 
    | Expr.Const (_, _, ty) -> ty
    | Expr.Val (vref, _, _) -> vref.Type
    | Expr.Sequential (a, b, k, _) -> tyOfExpr g (match k with NormalSeq -> b | ThenDoSeq -> a)
    | Expr.Lambda (_, _, _, vs, _, _, bodyTy) -> mkFunTy g (mkRefTupledVarsTy g vs) bodyTy
    | Expr.TyLambda (_, tyvs, _, _, bodyTy) -> (tyvs +-> bodyTy)
    | Expr.Let (_, e, _, _) 
    | Expr.TyChoose (_, e, _)
    | Expr.Link { contents=e}
    | Expr.DebugPoint (_, e)
    | Expr.StaticOptimization (_, _, e, _) 
    | Expr.LetRec (_, e, _, _) -> tyOfExpr g e
    | Expr.Op (op, tinst, _, _) -> 
        match op with 
        | TOp.Coerce -> (match tinst with [toTy;_fromTy] -> toTy | _ -> failwith "bad TOp.Coerce node")
        | TOp.ILCall (_, _, _, _, _, _, _, _, _, _, retTypes) | TOp.ILAsm (_, retTypes) -> (match retTypes with [h] -> h | _ -> g.unit_ty)
        | TOp.UnionCase uc -> actualResultTyOfUnionCase tinst uc 
        | TOp.UnionCaseProof uc -> mkProvenUnionCaseTy uc tinst  
        | TOp.Recd (_, tcref) -> mkWoNullAppTy tcref tinst
        | TOp.ExnConstr _ -> g.exn_ty
        | TOp.Bytes _ -> mkByteArrayTy g
        | TOp.UInt16s _ -> mkArrayType g g.uint16_ty
        | TOp.AnonRecdGet (_, i) -> List.item i tinst
        | TOp.TupleFieldGet (_, i) -> List.item i tinst
        | TOp.Tuple tupInfo -> mkAnyTupledTy g tupInfo tinst
        | TOp.AnonRecd anonInfo -> mkAnyAnonRecdTy g anonInfo tinst
        | TOp.IntegerForLoop _ | TOp.While _ -> g.unit_ty
        | TOp.Array -> (match tinst with [ty] -> mkArrayType g ty | _ -> failwith "bad TOp.Array node")
        | TOp.TryWith _ | TOp.TryFinally _ -> (match tinst with [ty] -> ty | _ -> failwith "bad TOp_try node")
        | TOp.ValFieldGetAddr (fref, readonly) -> mkByrefTyWithFlag g readonly (actualTyOfRecdFieldRef fref tinst)
        | TOp.ValFieldGet fref -> actualTyOfRecdFieldRef fref tinst
        | TOp.ValFieldSet _ | TOp.UnionCaseFieldSet _ | TOp.ExnFieldSet _ | TOp.LValueOp ((LSet | LByrefSet), _) ->g.unit_ty
        | TOp.UnionCaseTagGet _ -> g.int_ty
        | TOp.UnionCaseFieldGetAddr (cref, j, readonly) -> mkByrefTyWithFlag g readonly (actualTyOfRecdField (mkTyconRefInst cref.TyconRef tinst) (cref.FieldByIndex j))
        | TOp.UnionCaseFieldGet (cref, j) -> actualTyOfRecdField (mkTyconRefInst cref.TyconRef tinst) (cref.FieldByIndex j)
        | TOp.ExnFieldGet (ecref, j) -> recdFieldTyOfExnDefRefByIdx ecref j
        | TOp.LValueOp (LByrefGet, v) -> destByrefTy g v.Type
        | TOp.LValueOp (LAddrOf readonly, v) -> mkByrefTyWithFlag g readonly v.Type
        | TOp.RefAddrGet readonly -> (match tinst with [ty] -> mkByrefTyWithFlag g readonly ty | _ -> failwith "bad TOp.RefAddrGet node")      
        | TOp.TraitCall traitInfo -> traitInfo.GetReturnType(g)
        | TOp.Reraise -> (match tinst with [rtn_ty] -> rtn_ty | _ -> failwith "bad TOp.Reraise node")
        | TOp.Goto _ | TOp.Label _ | TOp.Return -> 
            //assert false
            //errorR(InternalError("unexpected goto/label/return in tyOfExpr", m))
            // It doesn't matter what type we return here. This is only used in free variable analysis in the code generator
            g.unit_ty
    | Expr.WitnessArg (traitInfo, _m) ->
        let witnessInfo = traitInfo.GetWitnessInfo()
        GenWitnessTy g witnessInfo

//--------------------------------------------------------------------------
// Make applications
//---------------------------------------------------------------------------

let primMkApp (f, fty) tyargs argsl m = 
  Expr.App (f, fty, tyargs, argsl, m)

// Check for the funky where a generic type instantiation at function type causes a generic function
// to appear to accept more arguments than it really does, e.g. "id id 1", where the first "id" is 
// instantiated with "int -> int".
//
// In this case, apply the arguments one at a time.
let isExpansiveUnderInstantiation g fty0 tyargs pargs argsl =
    isForallTy g fty0 && 
    let fty1 = formalApplyTys g fty0 (tyargs, pargs)
    (not (isFunTy g fty1) ||
     let rec loop fty xs = 
         match xs with 
         | [] -> false
         | _ :: t -> not (isFunTy g fty) || loop (rangeOfFunTy g fty) t
     loop fty1 argsl)
    
let mkExprAppAux g f fty argsl m =
    match argsl with 
    | [] -> f
    | _ -> 
        // Always combine the term application with a type application
        //
        // Combine the term application with a term application, but only when f' is an under-applied value of known arity
        match f with 
        | Expr.App (f0, fty0, tyargs, pargs, m2) 
             when
                 (isNil pargs ||
                  (match stripExpr f0 with 
                   | Expr.Val (v, _, _) -> 
                       match v.ValReprInfo with 
                       | Some info -> info.NumCurriedArgs > pargs.Length
                       | None -> false
                   | _ -> false)) &&
                 not (isExpansiveUnderInstantiation g fty0 tyargs pargs argsl) ->
            primMkApp (f0, fty0) tyargs (pargs@argsl) (unionRanges m2 m)

        | _ -> 
            // Don't combine. 'f' is not an application
            if not (isFunTy g fty) then error(InternalError("expected a function type", m))
            primMkApp (f, fty) [] argsl m

let rec mkAppsAux g f fty tyargsl argsl m =
  match tyargsl with 
  | tyargs :: rest -> 
      match tyargs with 
      | [] -> mkAppsAux g f fty rest argsl m
      | _ -> 
        let arfty = applyForallTy g fty tyargs
        mkAppsAux g (primMkApp (f, fty) tyargs [] m) arfty rest argsl m
  | [] -> 
      mkExprAppAux g f fty argsl m
      
let mkApps g ((f, fty), tyargsl, argl, m) = mkAppsAux g f fty tyargsl argl m

let mkTyAppExpr m (f, fty) tyargs = match tyargs with [] -> f | _ -> primMkApp (f, fty) tyargs [] m 

//--------------------------------------------------------------------------
// Decision tree reduction
//--------------------------------------------------------------------------

let rec accTargetsOfDecisionTree tree acc =
    match tree with 
    | TDSwitch (_, cases, dflt, _) -> 
        List.foldBack (fun (c: DecisionTreeCase) -> accTargetsOfDecisionTree c.CaseTree) cases 
            (Option.foldBack accTargetsOfDecisionTree dflt acc)
    | TDSuccess (_, i) -> i :: acc
    | TDBind (_, rest) -> accTargetsOfDecisionTree rest acc

let rec mapTargetsOfDecisionTree f tree =
    match tree with 
    | TDSwitch (e, cases, dflt, m) ->
        let casesR = cases |> List.map (mapTargetsOfDecisionTreeCase f) 
        let dfltR = Option.map (mapTargetsOfDecisionTree f) dflt
        TDSwitch (e, casesR, dfltR, m)
    | TDSuccess (es, i) -> TDSuccess(es, f i) 
    | TDBind (bind, rest) -> TDBind(bind, mapTargetsOfDecisionTree f rest)

and mapTargetsOfDecisionTreeCase f (TCase(x, t)) = 
    TCase(x, mapTargetsOfDecisionTree f t)

// Dead target elimination 
let eliminateDeadTargetsFromMatch tree (targets:_[]) =
    let used = accTargetsOfDecisionTree tree [] |> ListSet.setify (=) |> Array.ofList
    if used.Length < targets.Length then
        Array.sortInPlace used
        let ntargets = targets.Length
        let treeR = 
            let remap = Array.create ntargets -1
            Array.iteri (fun i tgn -> remap[tgn] <- i) used
            tree |> mapTargetsOfDecisionTree (fun tgn -> 
                 if remap[tgn] = -1 then failwith "eliminateDeadTargetsFromMatch: failure while eliminating unused targets"
                 remap[tgn]) 
        let targetsR = Array.map (Array.get targets) used
        treeR, targetsR
    else 
        tree, targets
    
let rec targetOfSuccessDecisionTree tree =
    match tree with 
    | TDSwitch _ -> None
    | TDSuccess (_, i) -> Some i
    | TDBind(_, t) -> targetOfSuccessDecisionTree t

/// Check a decision tree only has bindings that immediately cover a 'Success'
let rec decisionTreeHasNonTrivialBindings tree =
    match tree with 
    | TDSwitch (_, cases, dflt, _) -> 
        cases |> List.exists (fun c -> decisionTreeHasNonTrivialBindings c.CaseTree) || 
        dflt |> Option.exists decisionTreeHasNonTrivialBindings 
    | TDSuccess _ -> false
    | TDBind (_, t) -> Option.isNone (targetOfSuccessDecisionTree t)

// If a target has assignments and can only be reached through one 
// branch (i.e. is "linear"), then transfer the assignments to the r.h.s. to be a "let". 
let foldLinearBindingTargetsOfMatch tree (targets: _[]) =

    // Don't do this when there are any bindings in the tree except where those bindings immediately cover a success node
    // since the variables would be extruded from their scope. 
    if decisionTreeHasNonTrivialBindings tree then 
        tree, targets 

    else
        let branchesToTargets = Array.create targets.Length []
        // Build a map showing how each target might be reached
        let rec accumulateTipsOfDecisionTree accBinds tree =
            match tree with 
            | TDSwitch (_, cases, dflt, _) -> 
                assert (isNil accBinds)  // No switches under bindings
                for edge in cases do
                    accumulateTipsOfDecisionTree accBinds edge.CaseTree
                match dflt with 
                | None -> ()
                | Some tree -> accumulateTipsOfDecisionTree accBinds tree
            | TDSuccess (es, i) -> 
                branchesToTargets[i] <- (List.rev accBinds, es) :: branchesToTargets[i]
            | TDBind (bind, rest) -> 
                accumulateTipsOfDecisionTree (bind :: accBinds) rest 

        // Compute the targets that can only be reached one way
        accumulateTipsOfDecisionTree [] tree 
        let isLinearTarget bs = match bs with [_] -> true | _ -> false
        let isLinearTgtIdx i = isLinearTarget branchesToTargets[i] 
        let getLinearTgtIdx i = branchesToTargets[i].Head
        let hasLinearTgtIdx = branchesToTargets |> Array.exists isLinearTarget

        if not hasLinearTgtIdx then 

            tree, targets

        else
            
            /// rebuild the decision tree, replacing 'bind-then-success' decision trees by TDSuccess nodes that just go to the target
            let rec rebuildDecisionTree tree =
                
                // Check if this is a bind-then-success tree
                match targetOfSuccessDecisionTree tree with
                | Some i when isLinearTgtIdx i -> TDSuccess([], i)
                | _ -> 
                    match tree with 
                    | TDSwitch (e, cases, dflt, m) ->
                        let casesR = List.map rebuildDecisionTreeEdge cases
                        let dfltR = Option.map rebuildDecisionTree dflt
                        TDSwitch (e, casesR, dfltR, m)
                    | TDSuccess _ -> tree
                    | TDBind _ -> tree

            and rebuildDecisionTreeEdge (TCase(x, t)) =  
                TCase(x, rebuildDecisionTree t)

            let treeR = rebuildDecisionTree tree

            /// rebuild the targets, replacing linear targets by ones that include all the 'let' bindings from the source
            let targetsR = 
                targets |> Array.mapi (fun i (TTarget(vs, exprTarget, _) as tg) -> 
                    if isLinearTgtIdx i then
                        let binds, es = getLinearTgtIdx i
                        // The value bindings are moved to become part of the target.
                        // Hence the expressions in the value bindings can be remarked with the range of the target.
                        let mTarget = exprTarget.Range
                        let es = es |> List.map (remarkExpr mTarget)
                        // These are non-sticky - any sequence point for 'exprTarget' goes on 'exprTarget' _after_ the bindings have been evaluated
                        TTarget(List.empty, mkLetsBind mTarget binds (mkInvisibleLetsFromBindings mTarget vs es exprTarget), None)
                    else tg )
     
            treeR, targetsR

// Simplify a little as we go, including dead target elimination 
let simplifyTrivialMatch spBind mExpr mMatch ty tree (targets : _[]) = 
    match tree with 
    | TDSuccess(es, n) -> 
        if n >= targets.Length then failwith "simplifyTrivialMatch: target out of range"
        let (TTarget(vs, rhs, _)) = targets[n]
        if vs.Length <> es.Length then failwith ("simplifyTrivialMatch: invalid argument, n = " + string n + ", #targets = " + string targets.Length)

        // These are non-sticky - any sequence point for 'rhs' goes on 'rhs' _after_ the bindings have been made
        let res = mkInvisibleLetsFromBindings rhs.Range vs es rhs

        // Incorporate spBind as a note if present
        let res =
            match spBind with 
            | DebugPointAtBinding.Yes dp -> Expr.DebugPoint(DebugPointAtLeafExpr.Yes dp, res)
            | _ -> res
        res
    | _ -> 
        primMkMatch (spBind, mExpr, tree, targets, mMatch, ty)
 
// Simplify a little as we go, including dead target elimination 
let mkAndSimplifyMatch spBind mExpr mMatch ty tree targets = 
    let targets = Array.ofList targets
    match tree with 
    | TDSuccess _ -> 
        simplifyTrivialMatch spBind mExpr mMatch ty tree targets
    | _ -> 
        let tree, targets = eliminateDeadTargetsFromMatch tree targets
        let tree, targets = foldLinearBindingTargetsOfMatch tree targets
        simplifyTrivialMatch spBind mExpr mMatch ty tree targets

//-------------------------------------------------------------------------
// mkExprAddrOfExprAux
//------------------------------------------------------------------------- 

type Mutates = AddressOfOp | DefinitelyMutates | PossiblyMutates | NeverMutates
exception DefensiveCopyWarning of string * range

let isRecdOrStructTyconRefAssumedImmutable (g: TcGlobals) (tcref: TyconRef) =
    (tcref.CanDeref && not (isRecdOrUnionOrStructTyconRefDefinitelyMutable tcref)) ||
    tyconRefEq g tcref g.decimal_tcr || 
    tyconRefEq g tcref g.date_tcr

let isTyconRefReadOnly g m (tcref: TyconRef) =
    tcref.CanDeref &&
    if
        match tcref.TryIsReadOnly with 
        | ValueSome res -> res
        | _ ->
            let res = TyconRefHasAttribute g m g.attrib_IsReadOnlyAttribute tcref
            tcref.SetIsReadOnly res
            res 
    then true
    else tcref.IsEnumTycon

let isTyconRefAssumedReadOnly g (tcref: TyconRef) =
    tcref.CanDeref &&
    match tcref.TryIsAssumedReadOnly with 
    | ValueSome res -> res
    | _ -> 
        let res = isRecdOrStructTyconRefAssumedImmutable g tcref
        tcref.SetIsAssumedReadOnly res
        res

let isRecdOrStructTyconRefReadOnlyAux g m isInref (tcref: TyconRef) =
    if isInref && tcref.IsILStructOrEnumTycon then
        isTyconRefReadOnly g m tcref
    else
        isTyconRefReadOnly g m tcref || isTyconRefAssumedReadOnly g tcref

let isRecdOrStructTyconRefReadOnly g m tcref =
    isRecdOrStructTyconRefReadOnlyAux g m false tcref

let isRecdOrStructTyReadOnlyAux (g: TcGlobals) m isInref ty =
    match tryTcrefOfAppTy g ty with 
    | ValueNone -> false
    | ValueSome tcref -> isRecdOrStructTyconRefReadOnlyAux g m isInref tcref

let isRecdOrStructTyReadOnly g m ty =
    isRecdOrStructTyReadOnlyAux g m false ty

let CanTakeAddressOf g m isInref ty mut =
    match mut with 
    | NeverMutates -> true 
    | PossiblyMutates -> isRecdOrStructTyReadOnlyAux g m isInref ty
    | DefinitelyMutates -> false
    | AddressOfOp -> true // you can take the address but you might get a (readonly) inref<T> as a result

// We can take the address of values of struct type even if the value is immutable
// under certain conditions
//   - all instances of the type are known to be immutable; OR
//   - the operation is known not to mutate
//
// Note this may be taking the address of a closure field, i.e. a copy
// of the original struct, e.g. for
//    let f () = 
//        let g1 = A.G(1)
//        (fun () -> g1.x1)
//
// Note: isRecdOrStructTyReadOnly implies PossiblyMutates or NeverMutates
//
// We only do this for true local or closure fields because we can't take addresses of immutable static 
// fields across assemblies.
let CanTakeAddressOfImmutableVal (g: TcGlobals) m (vref: ValRef) mut =
    // We can take the address of values of struct type if the operation doesn't mutate 
    // and the value is a true local or closure field. 
    not vref.IsMutable &&
    not vref.IsMemberOrModuleBinding &&
    // Note: We can't add this:
    //    || valRefInThisAssembly g.compilingFSharpCore vref
    // This is because we don't actually guarantee to generate static backing fields for all values like these, e.g. simple constants "let x = 1".  
    // We always generate a static property but there is no field to take an address of
    CanTakeAddressOf g m false vref.Type mut

let MustTakeAddressOfVal (g: TcGlobals) (vref: ValRef) = 
    vref.IsMutable &&
    // We can only take the address of mutable values in the same assembly
    valRefInThisAssembly g.compilingFSharpCore vref

let MustTakeAddressOfByrefGet (g: TcGlobals) (vref: ValRef) = 
    isByrefTy g vref.Type && not (isInByrefTy g vref.Type)

let CanTakeAddressOfByrefGet (g: TcGlobals) (vref: ValRef) mut = 
    isInByrefTy g vref.Type &&
    CanTakeAddressOf g vref.Range true (destByrefTy g vref.Type) mut

let MustTakeAddressOfRecdField (rfref: RecdField) = 
    // Static mutable fields must be private, hence we don't have to take their address
    not rfref.IsStatic && 
    rfref.IsMutable

let MustTakeAddressOfRecdFieldRef (rfref: RecdFieldRef) = MustTakeAddressOfRecdField rfref.RecdField

let CanTakeAddressOfRecdFieldRef (g: TcGlobals) m (rfref: RecdFieldRef) tinst mut =
    // We only do this if the field is defined in this assembly because we can't take addresses across assemblies for immutable fields
    entityRefInThisAssembly g.compilingFSharpCore rfref.TyconRef &&
    not rfref.RecdField.IsMutable &&
    CanTakeAddressOf g m false (actualTyOfRecdFieldRef rfref tinst) mut

let CanTakeAddressOfUnionFieldRef (g: TcGlobals) m (uref: UnionCaseRef) cidx tinst mut =
    // We only do this if the field is defined in this assembly because we can't take addresses across assemblies for immutable fields
    entityRefInThisAssembly g.compilingFSharpCore uref.TyconRef &&
    let rfref = uref.FieldByIndex cidx
    not rfref.IsMutable &&
    CanTakeAddressOf g m false (actualTyOfUnionFieldRef uref cidx tinst) mut

let mkDerefAddrExpr mAddrGet expr mExpr exprTy =
    let v, _ = mkCompGenLocal mAddrGet "byrefReturn" exprTy
    mkCompGenLet mExpr v expr (mkAddrGet mAddrGet (mkLocalValRef v))

/// Make the address-of expression and return a wrapper that adds any allocated locals at an appropriate scope.
/// Also return a flag that indicates if the resulting pointer is a not a pointer where writing is allowed and will 
/// have intended effect (i.e. is a readonly pointer and/or a defensive copy).
let rec mkExprAddrOfExprAux g mustTakeAddress useReadonlyForGenericArrayAddress mut expr addrExprVal m =
    if mustTakeAddress then 
        let isNativePtr = 
            match addrExprVal with
            | Some vf -> valRefEq g vf g.addrof2_vref
            | _ -> false

        // If we are taking the native address using "&&" to get a nativeptr, disallow if it's readonly.
        let checkTakeNativeAddress readonly =
            if isNativePtr && readonly then
                error(Error(FSComp.SR.tastValueMustBeMutable(), m))

        match expr with 
        // LVALUE of "*x" where "x" is byref is just the byref itself
        | Expr.Op (TOp.LValueOp (LByrefGet, vref), _, [], m) when MustTakeAddressOfByrefGet g vref || CanTakeAddressOfByrefGet g vref mut -> 
            let readonly = not (MustTakeAddressOfByrefGet g vref)
            let writeonly = isOutByrefTy g vref.Type
            None, exprForValRef m vref, readonly, writeonly

        // LVALUE of "x" where "x" is mutable local, mutable intra-assembly module/static binding, or operation doesn't mutate.
        // Note: we can always take the address of mutable intra-assembly values
        | Expr.Val (vref, _, m) when MustTakeAddressOfVal g vref || CanTakeAddressOfImmutableVal g m vref mut ->
            let readonly = not (MustTakeAddressOfVal g vref)
            let writeonly = false
            checkTakeNativeAddress readonly
            None, mkValAddr m readonly vref, readonly, writeonly

        // LVALUE of "e.f" where "f" is an instance F# field or record field. 
        | Expr.Op (TOp.ValFieldGet rfref, tinst, [objExpr], m) when MustTakeAddressOfRecdFieldRef rfref || CanTakeAddressOfRecdFieldRef g m rfref tinst mut ->
            let objTy = tyOfExpr g objExpr
            let takeAddrOfObjExpr = isStructTy g objTy // It seems this will always be false - the address will already have been taken
            let wrap, expra, readonly, writeonly = mkExprAddrOfExprAux g takeAddrOfObjExpr false mut objExpr None m
            let readonly = readonly || isInByrefTy g objTy || not (MustTakeAddressOfRecdFieldRef rfref)
            let writeonly = writeonly || isOutByrefTy g objTy
            wrap, mkRecdFieldGetAddrViaExprAddr(readonly, expra, rfref, tinst, m), readonly, writeonly

        // LVALUE of "f" where "f" is a static F# field. 
        | Expr.Op (TOp.ValFieldGet rfref, tinst, [], m) when MustTakeAddressOfRecdFieldRef rfref || CanTakeAddressOfRecdFieldRef g m rfref tinst mut ->
            let readonly = not (MustTakeAddressOfRecdFieldRef rfref)
            let writeonly = false
            None, mkStaticRecdFieldGetAddr(readonly, rfref, tinst, m), readonly, writeonly

        // LVALUE of "e.f" where "f" is an F# union field. 
        | Expr.Op (TOp.UnionCaseFieldGet (uref, cidx), tinst, [objExpr], m) when MustTakeAddressOfRecdField (uref.FieldByIndex cidx) || CanTakeAddressOfUnionFieldRef g m uref cidx tinst mut ->
            let objTy = tyOfExpr g objExpr
            let takeAddrOfObjExpr = isStructTy g objTy // It seems this will always be false - the address will already have been taken
            let wrap, expra, readonly, writeonly = mkExprAddrOfExprAux g takeAddrOfObjExpr false mut objExpr None m
            let readonly = readonly || isInByrefTy g objTy || not (MustTakeAddressOfRecdField (uref.FieldByIndex cidx))
            let writeonly = writeonly || isOutByrefTy g objTy
            wrap, mkUnionCaseFieldGetAddrProvenViaExprAddr(readonly, expra, uref, tinst, cidx, m), readonly, writeonly

        // LVALUE of "f" where "f" is a .NET static field. 
        | Expr.Op (TOp.ILAsm ([I_ldsfld(_vol, fspec)], [ty2]), tinst, [], m) -> 
            let readonly = false // we never consider taking the address of a .NET static field to give an inref pointer
            let writeonly = false
            None, Expr.Op (TOp.ILAsm ([I_ldsflda fspec], [mkByrefTy g ty2]), tinst, [], m), readonly, writeonly

        // LVALUE of "e.f" where "f" is a .NET instance field. 
        | Expr.Op (TOp.ILAsm ([I_ldfld (_align, _vol, fspec)], [ty2]), tinst, [objExpr], m) -> 
            let objTy = tyOfExpr g objExpr
            let takeAddrOfObjExpr = isStructTy g objTy // It seems this will always be false - the address will already have been taken
            // we never consider taking the address of an .NET instance field to give an inref pointer, unless the object pointer is an inref pointer
            let wrap, expra, readonly, writeonly = mkExprAddrOfExprAux g takeAddrOfObjExpr false mut objExpr None m
            let readonly = readonly || isInByrefTy g objTy
            let writeonly = writeonly || isOutByrefTy g objTy
            wrap, Expr.Op (TOp.ILAsm ([I_ldflda fspec], [mkByrefTyWithFlag g readonly ty2]), tinst, [expra], m), readonly, writeonly

        // LVALUE of "e.[n]" where e is an array of structs 
        | Expr.App (Expr.Val (vf, _, _), _, [elemTy], [aexpr;nexpr], _) when (valRefEq g vf g.array_get_vref) -> 
      
            let readonly = false // array address is never forced to be readonly
            let writeonly = false
            let shape = ILArrayShape.SingleDimensional
            let ilInstrReadOnlyAnnotation = if isTyparTy g elemTy && useReadonlyForGenericArrayAddress then ReadonlyAddress else NormalAddress
            None, mkArrayElemAddress g (readonly, ilInstrReadOnlyAnnotation, isNativePtr, shape, elemTy, [aexpr; nexpr], m), readonly, writeonly

        // LVALUE of "e.[n1, n2]", "e.[n1, n2, n3]", "e.[n1, n2, n3, n4]" where e is an array of structs 
        | Expr.App (Expr.Val (vref, _, _), _, [elemTy], aexpr :: args, _) 
             when (valRefEq g vref g.array2D_get_vref || valRefEq g vref g.array3D_get_vref || valRefEq g vref g.array4D_get_vref) -> 
        
            let readonly = false // array address is never forced to be readonly
            let writeonly = false
            let shape = ILArrayShape.FromRank args.Length
            let ilInstrReadOnlyAnnotation = if isTyparTy g elemTy && useReadonlyForGenericArrayAddress then ReadonlyAddress else NormalAddress
            None, mkArrayElemAddress g (readonly, ilInstrReadOnlyAnnotation, isNativePtr, shape, elemTy, (aexpr :: args), m), readonly, writeonly

        // LVALUE: "&meth(args)" where meth has a byref or inref return. Includes "&span.[idx]".
        | Expr.Let (TBind(vref, e, _), Expr.Op (TOp.LValueOp (LByrefGet, vref2), _, _, _), _, _)  
             when (valRefEq g (mkLocalValRef vref) vref2) && 
                  (MustTakeAddressOfByrefGet g vref2 || CanTakeAddressOfByrefGet g vref2 mut) -> 
            let ty = tyOfExpr g e
            let readonly = isInByrefTy g ty
            let writeonly = isOutByrefTy g ty
            None, e, readonly, writeonly
        
        // Give a nice error message for address-of-byref
        | Expr.Val (vref, _, m) when isByrefTy g vref.Type -> 
            error(Error(FSComp.SR.tastUnexpectedByRef(), m))

        // Give a nice error message for DefinitelyMutates of address-of on mutable values in other assemblies
        | Expr.Val (vref, _, m) when (mut = DefinitelyMutates || mut = AddressOfOp) && vref.IsMutable -> 
            error(Error(FSComp.SR.tastInvalidAddressOfMutableAcrossAssemblyBoundary(), m))

        // Give a nice error message for AddressOfOp on immutable values
        | Expr.Val _ when mut = AddressOfOp -> 
            error(Error(FSComp.SR.tastValueMustBeLocal(), m))
         
        // Give a nice error message for mutating a value we can't take the address of
        | Expr.Val _ when mut = DefinitelyMutates -> 
            error(Error(FSComp.SR.tastValueMustBeMutable(), m))
         
        | _ -> 
            let ty = tyOfExpr g expr
            if isStructTy g ty then 
                match mut with 
                | NeverMutates
                | AddressOfOp -> ()
                | DefinitelyMutates -> 
                    // Give a nice error message for mutating something we can't take the address of
                    errorR(Error(FSComp.SR.tastInvalidMutationOfConstant(), m))
                | PossiblyMutates -> 
                    // Warn on defensive copy of something we can't take the address of
                    warning(DefensiveCopyWarning(FSComp.SR.tastValueHasBeenCopied(), m))

            match mut with
            | NeverMutates
            | DefinitelyMutates
            | PossiblyMutates -> ()
            | AddressOfOp -> 
                // we get an inref
                errorR(Error(FSComp.SR.tastCantTakeAddressOfExpression(), m))

            // Take a defensive copy
            let tmp, _ = 
                match mut with 
                | NeverMutates -> mkCompGenLocal m WellKnownNames.CopyOfStruct ty
                | _ -> mkMutableCompGenLocal m WellKnownNames.CopyOfStruct ty

            // This local is special in that it ignore byref scoping rules.
            tmp.SetIgnoresByrefScope()

            let readonly = true
            let writeonly = false
            Some (tmp, expr), (mkValAddr m readonly (mkLocalValRef tmp)), readonly, writeonly
    else
        None, expr, false, false

let mkExprAddrOfExpr g mustTakeAddress useReadonlyForGenericArrayAddress mut e addrExprVal m =
    let optBind, addre, readonly, writeonly = mkExprAddrOfExprAux g mustTakeAddress useReadonlyForGenericArrayAddress mut e addrExprVal m
    match optBind with 
    | None -> id, addre, readonly, writeonly
    | Some (tmp, rval) -> (fun x -> mkCompGenLet m tmp rval x), addre, readonly, writeonly

let mkTupleFieldGet g (tupInfo, e, tinst, i, m) = 
    let wrap, eR, _readonly, _writeonly = mkExprAddrOfExpr g (evalTupInfoIsStruct tupInfo) false NeverMutates e None m
    wrap (mkTupleFieldGetViaExprAddr(tupInfo, eR, tinst, i, m))

let mkAnonRecdFieldGet g (anonInfo: AnonRecdTypeInfo, e, tinst, i, m) = 
    let wrap, eR, _readonly, _writeonly = mkExprAddrOfExpr g (evalAnonInfoIsStruct anonInfo) false NeverMutates e None m
    wrap (mkAnonRecdFieldGetViaExprAddr(anonInfo, eR, tinst, i, m))

let mkRecdFieldGet g (e, fref: RecdFieldRef, tinst, m) = 
    assert (not (isByrefTy g (tyOfExpr g e)))
    let wrap, eR, _readonly, _writeonly = mkExprAddrOfExpr g fref.Tycon.IsStructOrEnumTycon false NeverMutates e None m
    wrap (mkRecdFieldGetViaExprAddr (eR, fref, tinst, m))

let mkUnionCaseFieldGetUnproven g (e, cref: UnionCaseRef, tinst, j, m) = 
    assert (not (isByrefTy g (tyOfExpr g e)))
    let wrap, eR, _readonly, _writeonly = mkExprAddrOfExpr g cref.Tycon.IsStructOrEnumTycon false NeverMutates e None m
    wrap (mkUnionCaseFieldGetUnprovenViaExprAddr (eR, cref, tinst, j, m))

let mkArray (argTy, args, m) = Expr.Op (TOp.Array, [argTy], args, m)

//---------------------------------------------------------------------------
// Compute fixups for letrec's.
//
// Generate an assignment expression that will fixup the recursion 
// amongst the vals on the r.h.s. of a letrec. The returned expressions 
// include disorderly constructs such as expressions/statements 
// to set closure environments and non-mutable fields. These are only ever 
// generated by the backend code-generator when processing a "letrec"
// construct.
//
// [self] is the top level value that is being fixed
// [exprToFix] is the r.h.s. expression
// [rvs] is the set of recursive vals being bound. 
// [acc] accumulates the expression right-to-left. 
//
// Traversal of the r.h.s. term must happen back-to-front to get the
// uniq's for the lambdas correct in the very rare case where the same lambda
// somehow appears twice on the right.
//---------------------------------------------------------------------------

let rec IterateRecursiveFixups g (selfv: Val option) rvs (access: Expr, set) exprToFix = 
    let exprToFix = stripExpr exprToFix
    match exprToFix with 
    | Expr.Const _ -> ()
    | Expr.Op (TOp.Tuple tupInfo, argTys, args, m) when not (evalTupInfoIsStruct tupInfo) ->
      args |> List.iteri (fun n -> 
          IterateRecursiveFixups g None rvs 
            (mkTupleFieldGet g (tupInfo, access, argTys, n, m), 
            (fun e -> 
              // NICE: it would be better to do this check in the type checker 
              errorR(Error(FSComp.SR.tastRecursiveValuesMayNotBeInConstructionOfTuple(), m))
              e)))

    | Expr.Op (TOp.UnionCase c, tinst, args, m) ->
      args |> List.iteri (fun n -> 
          IterateRecursiveFixups g None rvs 
            (mkUnionCaseFieldGetUnprovenViaExprAddr (access, c, tinst, n, m), 
             (fun e -> 
               // NICE: it would be better to do this check in the type checker 
               let tcref = c.TyconRef
               if not (c.FieldByIndex n).IsMutable && not (entityRefInThisAssembly g.compilingFSharpCore tcref) then
                 errorR(Error(FSComp.SR.tastRecursiveValuesMayNotAppearInConstructionOfType(tcref.LogicalName), m))
               mkUnionCaseFieldSet (access, c, tinst, n, e, m))))

    | Expr.Op (TOp.Recd (_, tcref), tinst, args, m) -> 
      (tcref.TrueInstanceFieldsAsRefList, args) ||> List.iter2 (fun fref arg -> 
          let fspec = fref.RecdField
          IterateRecursiveFixups g None rvs 
            (mkRecdFieldGetViaExprAddr (access, fref, tinst, m), 
             (fun e -> 
               // NICE: it would be better to do this check in the type checker 
               if not fspec.IsMutable && not (entityRefInThisAssembly g.compilingFSharpCore tcref) then
                 errorR(Error(FSComp.SR.tastRecursiveValuesMayNotBeAssignedToNonMutableField(fspec.rfield_id.idText, tcref.LogicalName), m))
               mkRecdFieldSetViaExprAddr (access, fref, tinst, e, m))) arg )
    | Expr.Val _
    | Expr.Lambda _
    | Expr.Obj _
    | Expr.TyChoose _
    | Expr.TyLambda _ -> 
        rvs selfv access set exprToFix
    | _ -> ()

//--------------------------------------------------------------------------
// computations on constraints
//-------------------------------------------------------------------------- 

let JoinTyparStaticReq r1 r2 = 
  match r1, r2 with
  | TyparStaticReq.None, r | r, TyparStaticReq.None -> r 
  | TyparStaticReq.HeadType, r | r, TyparStaticReq.HeadType -> r
  
//-------------------------------------------------------------------------
// ExprFolder - fold steps
//-------------------------------------------------------------------------

type ExprFolder<'State> = 
    { exprIntercept : (* recurseF *) ('State -> Expr -> 'State) -> (* noInterceptF *) ('State -> Expr -> 'State) -> 'State -> Expr -> 'State
      // the bool is 'bound in dtree' 
      valBindingSiteIntercept : 'State -> bool * Val -> 'State               
      // these values are always bound to these expressions. bool indicates 'recursively' 
      nonRecBindingsIntercept : 'State -> Binding -> 'State   
      recBindingsIntercept : 'State -> Bindings -> 'State        
      dtreeIntercept : 'State -> DecisionTree -> 'State                    
      targetIntercept : (* recurseF *) ('State -> Expr -> 'State) -> 'State -> DecisionTreeTarget -> 'State option 
      tmethodIntercept : (* recurseF *) ('State -> Expr -> 'State) -> 'State -> ObjExprMethod -> 'State option
    }

let ExprFolder0 =
    { exprIntercept = (fun _recurseF noInterceptF z x -> noInterceptF z x)
      valBindingSiteIntercept = (fun z _b -> z)
      nonRecBindingsIntercept = (fun z _bs -> z)
      recBindingsIntercept = (fun z _bs -> z)
      dtreeIntercept = (fun z _dt -> z)
      targetIntercept = (fun _exprF _z _x -> None)
      tmethodIntercept = (fun _exprF _z _x -> None) }

//-------------------------------------------------------------------------
// FoldExpr
//-------------------------------------------------------------------------

/// Adapted from usage info folding.
/// Collecting from exprs at moment.
/// To collect ids etc some additional folding needed, over formals etc.
type ExprFolders<'State> (folders: ExprFolder<'State>) =
    let mutable exprFClosure = Unchecked.defaultof<'State -> Expr -> 'State> // prevent reallocation of closure
    let mutable exprNoInterceptFClosure = Unchecked.defaultof<'State -> Expr -> 'State> // prevent reallocation of closure
    let stackGuard = StackGuard(FoldExprStackGuardDepth, "FoldExprStackGuardDepth")

    let rec exprsF z xs = 
        List.fold exprFClosure z xs

    and exprF (z: 'State) (x: Expr) =
        stackGuard.Guard <| fun () ->
        folders.exprIntercept exprFClosure exprNoInterceptFClosure z x 

    and exprNoInterceptF (z: 'State) (x: Expr) = 
        match x with
        
        | Expr.Const _ -> z

        | Expr.Val _ -> z

        | LinearOpExpr (_op, _tyargs, argsHead, argLast, _m) ->
            let z = exprsF z argsHead
            // tailcall 
            exprF z argLast
        
        | Expr.Op (_c, _tyargs, args, _) -> 
            exprsF z args

        | Expr.Sequential (x0, x1, _dir, _) -> 
            let z = exprF z x0
            exprF z x1

        | Expr.Lambda (_lambdaId, _ctorThisValOpt, _baseValOpt, _argvs, body, _m, _rty) -> 
            exprF z body

        | Expr.TyLambda (_lambdaId, _tps, body, _m, _rty) -> 
            exprF z body

        | Expr.TyChoose (_, body, _) -> 
            exprF z body

        | Expr.App (f, _fty, _tys, argTys, _) -> 
            let z = exprF z f
            exprsF z argTys
                
        | Expr.LetRec (binds, body, _, _) -> 
            let z = valBindsF false z binds
            exprF z body
                
        | Expr.Let (bind, body, _, _) -> 
            let z = valBindF false z bind
            exprF z body
                
        | Expr.Link rX -> exprF z rX.Value

        | Expr.DebugPoint (_, innerExpr) -> exprF z innerExpr

        | Expr.Match (_spBind, _exprm, dtree, targets, _m, _ty) -> 
            let z = dtreeF z dtree
            let z = Array.fold targetF z targets[0..targets.Length - 2]
            // tailcall
            targetF z targets[targets.Length - 1]
                
        | Expr.Quote (e, dataCell, _, _, _) -> 
            let z = exprF z e
            match dataCell.Value with 
            | None -> z
            | Some ((_typeDefs, _argTypes, argExprs, _), _) -> exprsF z argExprs

        | Expr.Obj (_n, _typ, _basev, basecall, overrides, iimpls, _m) -> 
            let z = exprF z basecall
            let z = List.fold tmethodF z overrides
            List.fold (foldOn snd (List.fold tmethodF)) z iimpls

        | Expr.StaticOptimization (_tcs, csx, x, _) -> 
            exprsF z [csx;x]

        | Expr.WitnessArg (_witnessInfo, _m) ->
            z

    and valBindF dtree z bind =
        let z = folders.nonRecBindingsIntercept z bind
        bindF dtree z bind 

    and valBindsF dtree z binds =
        let z = folders.recBindingsIntercept z binds
        List.fold (bindF dtree) z binds 

    and bindF dtree z (bind: Binding) =
        let z = folders.valBindingSiteIntercept z (dtree, bind.Var)
        exprF z bind.Expr

    and dtreeF z dtree =
        let z = folders.dtreeIntercept z dtree
        match dtree with
        | TDBind (bind, rest) -> 
            let z = valBindF true z bind
            dtreeF z rest
        | TDSuccess (args, _) -> exprsF z args
        | TDSwitch (test, dcases, dflt, _) -> 
            let z = exprF z test
            let z = List.fold dcaseF z dcases
            let z = Option.fold dtreeF z dflt
            z

    and dcaseF z = function
        TCase (_, dtree) -> dtreeF z dtree (* not collecting from test *)

    and targetF z x =
        match folders.targetIntercept exprFClosure z x with 
        | Some z -> z // intercepted 
        | None ->     // structurally recurse 
            let (TTarget (_, body, _)) = x
            exprF z body
              
    and tmethodF z x =
        match folders.tmethodIntercept exprFClosure z x with 
        | Some z -> z // intercepted 
        | None ->     // structurally recurse 
            let (TObjExprMethod(_, _, _, _, e, _)) = x
            exprF z e

    and mdefF z x = 
        match x with
        | TMDefRec(_, _, _, mbinds, _) -> 
            // REVIEW: also iterate the abstract slot vspecs hidden in the _vslots field in the tycons
            let z = List.fold mbindF z mbinds
            z
        | TMDefLet(bind, _) -> valBindF false z bind
        | TMDefOpens _ -> z
        | TMDefDo(e, _) -> exprF z e
        | TMDefs defs -> List.fold mdefF z defs 

    and mbindF z x = 
        match x with 
        | ModuleOrNamespaceBinding.Binding b -> valBindF false z b
        | ModuleOrNamespaceBinding.Module(_, def) -> mdefF z def

    let implF z (x: CheckedImplFile) =
        mdefF z x.Contents

    do exprFClosure <- exprF // allocate one instance of this closure
    do exprNoInterceptFClosure <- exprNoInterceptF // allocate one instance of this closure

    member x.FoldExpr = exprF

    member x.FoldImplFile = implF

let FoldExpr folders state expr = ExprFolders(folders).FoldExpr state expr

let FoldImplFile folders state implFile = ExprFolders(folders).FoldImplFile state implFile 

#if DEBUG
//-------------------------------------------------------------------------
// ExprStats
//-------------------------------------------------------------------------

let ExprStats x =
  let mutable count = 0
  let folders = {ExprFolder0 with exprIntercept = (fun _ noInterceptF z x -> (count <- count + 1; noInterceptF z x))}
  let () = FoldExpr folders () x
  string count + " TExpr nodes"
#endif
    
//-------------------------------------------------------------------------
// Make expressions
//------------------------------------------------------------------------- 

let mkString (g: TcGlobals) m n = Expr.Const (Const.String n, m, g.string_ty)

let mkByte (g: TcGlobals) m b = Expr.Const (Const.Byte b, m, g.byte_ty)

let mkUInt16 (g: TcGlobals) m b = Expr.Const (Const.UInt16 b, m, g.uint16_ty)

let mkUnit (g: TcGlobals) m = Expr.Const (Const.Unit, m, g.unit_ty)

let mkInt32 (g: TcGlobals) m n = Expr.Const (Const.Int32 n, m, g.int32_ty)

let mkInt g m n = mkInt32 g m n

let mkZero g m = mkInt g m 0

let mkOne g m = mkInt g m 1

let mkTwo g m = mkInt g m 2

let mkMinusOne g m = mkInt g m -1

let mkTypedZero g m ty =
    if typeEquivAux EraseMeasures g ty g.int32_ty then Expr.Const (Const.Int32 0, m, ty)
    elif typeEquivAux EraseMeasures g ty g.int64_ty then Expr.Const (Const.Int64 0L, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint64_ty then Expr.Const (Const.UInt64 0UL, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint32_ty then Expr.Const (Const.UInt32 0u, m, ty)
    elif typeEquivAux EraseMeasures g ty g.nativeint_ty then Expr.Const (Const.IntPtr 0L, m, ty)
    elif typeEquivAux EraseMeasures g ty g.unativeint_ty then Expr.Const (Const.UIntPtr 0UL, m, ty)
    elif typeEquivAux EraseMeasures g ty g.int16_ty then Expr.Const (Const.Int16 0s, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint16_ty then Expr.Const (Const.UInt16 0us, m, ty)
    elif typeEquivAux EraseMeasures g ty g.sbyte_ty then Expr.Const (Const.SByte 0y, m, ty)
    elif typeEquivAux EraseMeasures g ty g.byte_ty then Expr.Const (Const.Byte 0uy, m, ty)
    elif typeEquivAux EraseMeasures g ty g.char_ty then Expr.Const (Const.Char '\000', m, ty)
    elif typeEquivAux EraseMeasures g ty g.float32_ty then Expr.Const (Const.Single 0.0f, m, ty)
    elif typeEquivAux EraseMeasures g ty g.float_ty then Expr.Const (Const.Double 0.0, m, ty)
    elif typeEquivAux EraseMeasures g ty g.decimal_ty then Expr.Const (Const.Decimal 0m, m, ty)
    else error (InternalError ($"Unrecognized numeric type '{ty}'.", m))

let mkTypedOne g m ty =
    if typeEquivAux EraseMeasures g ty g.int32_ty then Expr.Const (Const.Int32 1, m, ty)
    elif typeEquivAux EraseMeasures g ty g.int64_ty then Expr.Const (Const.Int64 1L, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint64_ty then Expr.Const (Const.UInt64 1UL, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint32_ty then Expr.Const (Const.UInt32 1u, m, ty)
    elif typeEquivAux EraseMeasures g ty g.nativeint_ty then Expr.Const (Const.IntPtr 1L, m, ty)
    elif typeEquivAux EraseMeasures g ty g.unativeint_ty then Expr.Const (Const.UIntPtr 1UL, m, ty)
    elif typeEquivAux EraseMeasures g ty g.int16_ty then Expr.Const (Const.Int16 1s, m, ty)
    elif typeEquivAux EraseMeasures g ty g.uint16_ty then Expr.Const (Const.UInt16 1us, m, ty)
    elif typeEquivAux EraseMeasures g ty g.sbyte_ty then Expr.Const (Const.SByte 1y, m, ty)
    elif typeEquivAux EraseMeasures g ty g.byte_ty then Expr.Const (Const.Byte 1uy, m, ty)
    elif typeEquivAux EraseMeasures g ty g.char_ty then Expr.Const (Const.Char '\001', m, ty)
    elif typeEquivAux EraseMeasures g ty g.float32_ty then Expr.Const (Const.Single 1.0f, m, ty)
    elif typeEquivAux EraseMeasures g ty g.float_ty then Expr.Const (Const.Double 1.0, m, ty)
    elif typeEquivAux EraseMeasures g ty g.decimal_ty then Expr.Const (Const.Decimal 1m, m, ty)
    else error (InternalError ($"Unrecognized numeric type '{ty}'.", m))

let destInt32 = function Expr.Const (Const.Int32 n, _, _) -> Some n | _ -> None

let isIDelegateEventType g ty =
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tyconRefEq g g.fslib_IDelegateEvent_tcr tcref
    | _ -> false

let destIDelegateEventType g ty = 
    if isIDelegateEventType g ty then 
        match argsOfAppTy g ty with 
        | [ty1] -> ty1
        | _ -> failwith "destIDelegateEventType: internal error"
    else failwith "destIDelegateEventType: not an IDelegateEvent type"

let mkIEventType (g: TcGlobals) ty1 ty2 = TType_app (g.fslib_IEvent2_tcr, [ty1;ty2], g.knownWithoutNull)

let mkIObservableType (g: TcGlobals) ty1 = TType_app (g.tcref_IObservable, [ty1], g.knownWithoutNull)

let mkIObserverType (g: TcGlobals) ty1 = TType_app (g.tcref_IObserver, [ty1], g.knownWithoutNull)

let mkRefCellContentsRef (g: TcGlobals) = mkRecdFieldRef g.refcell_tcr_canon "contents"

let mkSequential m e1 e2 = Expr.Sequential (e1, e2, NormalSeq, m)

let mkCompGenSequential m stmt expr = mkSequential m stmt expr

let mkThenDoSequential m expr stmt = Expr.Sequential (expr, stmt, ThenDoSeq, m)

let mkCompGenThenDoSequential m expr stmt = mkThenDoSequential m expr stmt

let rec mkSequentials g m es = 
    match es with 
    | [e] -> e 
    | e :: es -> mkSequential m e (mkSequentials g m es) 
    | [] -> mkUnit g m

let mkGetArg0 m ty = mkAsmExpr ( [ mkLdarg0 ], [], [], [ty], m) 

//-------------------------------------------------------------------------
// Tuples...
//------------------------------------------------------------------------- 
 
let mkAnyTupled g m tupInfo es tys = 
    match es with 
    | [] -> mkUnit g m 
    | [e] -> e
    | _ -> Expr.Op (TOp.Tuple tupInfo, tys, es, m)

let mkRefTupled g m es tys = mkAnyTupled g m tupInfoRef es tys

let mkRefTupledNoTypes g m args = mkRefTupled g m args (List.map (tyOfExpr g) args)

let mkRefTupledVars g m vs = mkRefTupled g m (List.map (exprForVal m) vs) (typesOfVals vs)

//--------------------------------------------------------------------------
// Permute expressions
//--------------------------------------------------------------------------
    
let inversePerm (sigma: int array) =
    let n = sigma.Length
    let invSigma = Array.create n -1
    for i = 0 to n-1 do
        let sigma_i = sigma[i]
        // assert( invSigma.[sigma_i] = -1 )
        invSigma[sigma_i] <- i
    invSigma
  
let permute (sigma: int[]) (data:'T[]) = 
    let n = sigma.Length
    let invSigma = inversePerm sigma
    Array.init n (fun i -> data[invSigma[i]])
  
let rec existsR a b pred = if a<=b then pred a || existsR (a+1) b pred else false

// Given a permutation for record fields, work out the highest entry that we must lift out
// of a record initialization. Lift out xi if xi goes to position that will be preceded by an expr with an effect 
// that originally followed xi. If one entry gets lifted then everything before it also gets lifted.
let liftAllBefore sigma = 
    let invSigma = inversePerm sigma

    let lifted = 
        [ for i in 0 .. sigma.Length - 1 do 
            let iR = sigma[i]
            if existsR 0 (iR - 1) (fun jR -> invSigma[jR] > i) then 
                    yield i ]

    if lifted.IsEmpty then 0 else List.max lifted + 1


///  Put record field assignments in order.
//
let permuteExprList (sigma: int[]) (exprs: Expr list) (ty: TType list) (names: string list) =
    let ty, names = (Array.ofList ty, Array.ofList names)

    let liftLim = liftAllBefore sigma 

    let rewrite rbinds (i, expri: Expr) =
        if i < liftLim then
            let tmpvi, tmpei = mkCompGenLocal expri.Range names[i] ty[i]
            let bindi = mkCompGenBind tmpvi expri
            tmpei, bindi :: rbinds
        else
            expri, rbinds
 
    let newExprs, reversedBinds = List.mapFold rewrite [] (exprs |> List.indexed)
    let binds = List.rev reversedBinds
    let reorderedExprs = permute sigma (Array.ofList newExprs)
    binds, Array.toList reorderedExprs
    
/// Evaluate the expressions in the original order, but build a record with the results in field order 
/// Note some fields may be static. If this were not the case we could just use 
///     let sigma = Array.map #Index ()  
/// However the presence of static fields means .Index may index into a non-compact set of instance field indexes. 
/// We still need to sort by index. 
let mkRecordExpr g (lnk, tcref, tinst, unsortedRecdFields: RecdFieldRef list, unsortedFieldExprs, m) =  
    // Remove any abbreviations 
    let tcref, tinst = destAppTy g (mkWoNullAppTy tcref tinst)
    
    let sortedRecdFields = unsortedRecdFields |> List.indexed |> Array.ofList |> Array.sortBy (fun (_, r) -> r.Index)
    let sigma = Array.create sortedRecdFields.Length -1
    sortedRecdFields |> Array.iteri (fun sortedIdx (unsortedIdx, _) -> 
        if sigma[unsortedIdx] <> -1 then error(InternalError("bad permutation", m))
        sigma[unsortedIdx] <- sortedIdx) 
    
    let unsortedArgTys = unsortedRecdFields |> List.map (fun rfref -> actualTyOfRecdFieldRef rfref tinst)
    let unsortedArgNames = unsortedRecdFields |> List.map (fun rfref -> rfref.FieldName)
    let unsortedArgBinds, sortedArgExprs = permuteExprList sigma unsortedFieldExprs unsortedArgTys unsortedArgNames
    let core = Expr.Op (TOp.Recd (lnk, tcref), tinst, sortedArgExprs, m)
    mkLetsBind m unsortedArgBinds core

let mkAnonRecd (_g: TcGlobals) m (anonInfo: AnonRecdTypeInfo) (unsortedIds: Ident[]) (unsortedFieldExprs: Expr list) unsortedArgTys =
    let sortedRecdFields = unsortedFieldExprs |> List.indexed |> Array.ofList |> Array.sortBy (fun (i,_) -> unsortedIds[i].idText)
    let sortedArgTys = unsortedArgTys |> List.indexed |> List.sortBy (fun (i,_) -> unsortedIds[i].idText) |> List.map snd

    let sigma = Array.create sortedRecdFields.Length -1
    sortedRecdFields |> Array.iteri (fun sortedIdx (unsortedIdx, _) -> 
        if sigma[unsortedIdx] <> -1 then error(InternalError("bad permutation", m))
        sigma[unsortedIdx] <- sortedIdx) 
    
    let unsortedArgNames = unsortedIds |> Array.toList |> List.map (fun id -> id.idText)
    let unsortedArgBinds, sortedArgExprs = permuteExprList sigma unsortedFieldExprs unsortedArgTys unsortedArgNames
    let core = Expr.Op (TOp.AnonRecd anonInfo, sortedArgTys, sortedArgExprs, m)
    mkLetsBind m unsortedArgBinds core
  
//-------------------------------------------------------------------------
// List builders
//------------------------------------------------------------------------- 
 
let mkRefCell g m ty e = mkRecordExpr g (RecdExpr, g.refcell_tcr_canon, [ty], [mkRefCellContentsRef g], [e], m)

let mkRefCellGet g m ty e = mkRecdFieldGetViaExprAddr (e, mkRefCellContentsRef g, [ty], m)

let mkRefCellSet g m ty e1 e2 = mkRecdFieldSetViaExprAddr (e1, mkRefCellContentsRef g, [ty], e2, m)

let mkNil (g: TcGlobals) m ty = mkUnionCaseExpr (g.nil_ucref, [ty], [], m)

let mkCons (g: TcGlobals) ty h t = mkUnionCaseExpr (g.cons_ucref, [ty], [h;t], unionRanges h.Range t.Range)

let mkCompGenLocalAndInvisibleBind g nm m e = 
    let locv, loce = mkCompGenLocal m nm (tyOfExpr g e)
    locv, loce, mkInvisibleBind locv e 

//----------------------------------------------------------------------------
// Make some fragments of code
//----------------------------------------------------------------------------

let box = I_box (mkILTyvarTy 0us)

let isinst = I_isinst (mkILTyvarTy 0us)

let unbox = I_unbox_any (mkILTyvarTy 0us)

let mkUnbox ty e m = mkAsmExpr ([ unbox ], [ty], [e], [ ty ], m)

let mkBox ty e m = mkAsmExpr ([box], [], [e], [ty], m)

let mkIsInst ty e m = mkAsmExpr ([ isinst ], [ty], [e], [ ty ], m)

let mspec_Type_GetTypeFromHandle (g: TcGlobals) = mkILNonGenericStaticMethSpecInTy(g.ilg.typ_Type, "GetTypeFromHandle", [g.iltyp_RuntimeTypeHandle], g.ilg.typ_Type)

let mspec_String_Length (g: TcGlobals) = mkILNonGenericInstanceMethSpecInTy (g.ilg.typ_String, "get_Length", [], g.ilg.typ_Int32)

let mspec_String_Concat2 (g: TcGlobals) = 
    mkILNonGenericStaticMethSpecInTy (g.ilg.typ_String, "Concat", [ g.ilg.typ_String; g.ilg.typ_String ], g.ilg.typ_String)

let mspec_String_Concat3 (g: TcGlobals) = 
    mkILNonGenericStaticMethSpecInTy (g.ilg.typ_String, "Concat", [ g.ilg.typ_String; g.ilg.typ_String; g.ilg.typ_String ], g.ilg.typ_String)

let mspec_String_Concat4 (g: TcGlobals) = 
    mkILNonGenericStaticMethSpecInTy (g.ilg.typ_String, "Concat", [ g.ilg.typ_String; g.ilg.typ_String; g.ilg.typ_String; g.ilg.typ_String ], g.ilg.typ_String)

let mspec_String_Concat_Array (g: TcGlobals) = 
    mkILNonGenericStaticMethSpecInTy (g.ilg.typ_String, "Concat", [ mkILArr1DTy g.ilg.typ_String ], g.ilg.typ_String)

let fspec_Missing_Value (g: TcGlobals) = mkILFieldSpecInTy(g.iltyp_Missing, "Value", g.iltyp_Missing)

let mkInitializeArrayMethSpec (g: TcGlobals) = 
  let tref = g.FindSysILTypeRef "System.Runtime.CompilerServices.RuntimeHelpers"
  mkILNonGenericStaticMethSpecInTy(mkILNonGenericBoxedTy tref, "InitializeArray", [g.ilg.typ_Array;g.iltyp_RuntimeFieldHandle], ILType.Void)

let mkInvalidCastExnNewobj (g: TcGlobals) = 
  mkNormalNewobj (mkILCtorMethSpecForTy (mkILNonGenericBoxedTy (g.FindSysILTypeRef "System.InvalidCastException"), []))

let typedExprForIntrinsic _g m (IntrinsicValRef(_, _, _, ty, _) as i) =
    let vref = ValRefForIntrinsic i
    exprForValRef m vref, ty

let mkCallGetGenericComparer (g: TcGlobals) m = typedExprForIntrinsic g m g.get_generic_comparer_info |> fst

let mkCallGetGenericEREqualityComparer (g: TcGlobals) m = typedExprForIntrinsic g m g.get_generic_er_equality_comparer_info |> fst

let mkCallGetGenericPEREqualityComparer (g: TcGlobals) m = typedExprForIntrinsic g m g.get_generic_per_equality_comparer_info |> fst

let mkCallUnbox (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.unbox_info, [[ty]], [ e1 ], m)

let mkCallUnboxFast (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.unbox_fast_info, [[ty]], [ e1 ], m)

let mkCallTypeTest (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.istype_info, [[ty]], [ e1 ], m)

let mkCallTypeOf (g: TcGlobals) m ty = mkApps g (typedExprForIntrinsic g m g.typeof_info, [[ty]], [ ], m)

let mkCallTypeDefOf (g: TcGlobals) m ty = mkApps g (typedExprForIntrinsic g m g.typedefof_info, [[ty]], [ ], m)
 
let mkCallDispose (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.dispose_info, [[ty]], [ e1 ], m)

let mkCallSeq (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.seq_info, [[ty]], [ e1 ], m)

let mkCallCreateInstance (g: TcGlobals) m ty = mkApps g (typedExprForIntrinsic g m g.create_instance_info, [[ty]], [ mkUnit g m ], m)

let mkCallGetQuerySourceAsEnumerable (g: TcGlobals) m ty1 ty2 e1 = mkApps g (typedExprForIntrinsic g m g.query_source_as_enum_info, [[ty1;ty2]], [ e1; mkUnit g m ], m)

let mkCallNewQuerySource (g: TcGlobals) m ty1 ty2 e1 = mkApps g (typedExprForIntrinsic g m g.new_query_source_info, [[ty1;ty2]], [ e1 ], m)

let mkCallCreateEvent (g: TcGlobals) m ty1 ty2 e1 e2 e3 = mkApps g (typedExprForIntrinsic g m g.create_event_info, [[ty1;ty2]], [ e1;e2;e3 ], m)

let mkCallGenericComparisonWithComparerOuter (g: TcGlobals) m ty comp e1 e2 = mkApps g (typedExprForIntrinsic g m g.generic_comparison_withc_outer_info, [[ty]], [ comp;e1;e2 ], m)

let mkCallGenericEqualityEROuter (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.generic_equality_er_outer_info, [[ty]], [ e1;e2 ], m)

let mkCallGenericEqualityWithComparerOuter (g: TcGlobals) m ty comp e1 e2 = mkApps g (typedExprForIntrinsic g m g.generic_equality_withc_outer_info, [[ty]], [comp;e1;e2], m)

let mkCallGenericHashWithComparerOuter (g: TcGlobals) m ty comp e1 = mkApps g (typedExprForIntrinsic g m g.generic_hash_withc_outer_info, [[ty]], [comp;e1], m)

let mkCallEqualsOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.equals_operator_info, [[ty]], [ e1;e2 ], m)

let mkCallNotEqualsOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.not_equals_operator, [[ty]], [ e1;e2 ], m)

let mkCallLessThanOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.less_than_operator, [[ty]], [ e1;e2 ], m)

let mkCallLessThanOrEqualsOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.less_than_or_equals_operator, [[ty]], [ e1;e2 ], m)

let mkCallGreaterThanOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.greater_than_operator, [[ty]], [ e1;e2 ], m)

let mkCallGreaterThanOrEqualsOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.greater_than_or_equals_operator, [[ty]], [ e1;e2 ], m)

let mkCallAdditionOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.unchecked_addition_info, [[ty; ty; ty]], [e1;e2], m)

let mkCallSubtractionOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.unchecked_subtraction_info, [[ty; ty; ty]], [e1;e2], m)

let mkCallMultiplyOperator (g: TcGlobals) m ty1 ty2 retTy e1 e2 = mkApps g (typedExprForIntrinsic g m g.unchecked_multiply_info, [[ty1; ty2; retTy]], [e1;e2], m)

let mkCallDivisionOperator (g: TcGlobals) m ty1 ty2 retTy e1 e2 = mkApps g (typedExprForIntrinsic g m g.unchecked_division_info, [[ty1; ty2; retTy]], [e1;e2], m)

let mkCallModulusOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.unchecked_modulus_info, [[ty; ty; ty]], [e1;e2], m)

let mkCallDefaultOf (g: TcGlobals) m ty = mkApps g (typedExprForIntrinsic g m g.unchecked_defaultof_info, [[ty]], [], m)

let mkCallBitwiseAndOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.bitwise_and_info, [[ty]], [e1;e2], m)

let mkCallBitwiseOrOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.bitwise_or_info, [[ty]], [e1;e2], m)

let mkCallBitwiseXorOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.bitwise_xor_info, [[ty]], [e1;e2], m)

let mkCallShiftLeftOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.bitwise_shift_left_info, [[ty]], [e1;e2], m)

let mkCallShiftRightOperator (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.bitwise_shift_right_info, [[ty]], [e1;e2], m)

let mkCallUnaryNegOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.unchecked_unary_minus_info, [[ty]], [e1], m)

let mkCallUnaryNotOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.bitwise_unary_not_info, [[ty]], [e1], m)

let mkCallAdditionChecked (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.checked_addition_info, [[ty; ty; ty]], [e1;e2], m)

let mkCallSubtractionChecked (g: TcGlobals) m ty e1 e2 = mkApps g (typedExprForIntrinsic g m g.checked_subtraction_info, [[ty; ty; ty]], [e1;e2], m)

let mkCallMultiplyChecked (g: TcGlobals) m ty1 ty2 retTy e1 e2 = mkApps g (typedExprForIntrinsic g m g.checked_multiply_info, [[ty1; ty2; retTy]], [e1;e2], m)

let mkCallUnaryNegChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.checked_unary_minus_info, [[ty]], [e1], m)

let mkCallToByteChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.byte_checked_info, [[ty]], [e1], m)

let mkCallToSByteChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.sbyte_checked_info, [[ty]], [e1], m)

let mkCallToInt16Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int16_checked_info, [[ty]], [e1], m)

let mkCallToUInt16Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint16_checked_info, [[ty]], [e1], m)

let mkCallToIntChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int_checked_info, [[ty]], [e1], m)

let mkCallToInt32Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int32_checked_info, [[ty]], [e1], m)

let mkCallToUInt32Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint32_checked_info, [[ty]], [e1], m)

let mkCallToInt64Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int64_checked_info, [[ty]], [e1], m)

let mkCallToUInt64Checked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint64_checked_info, [[ty]], [e1], m)

let mkCallToIntPtrChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.nativeint_checked_info, [[ty]], [e1], m)

let mkCallToUIntPtrChecked (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.unativeint_checked_info, [[ty]], [e1], m)

let mkCallToByteOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.byte_operator_info, [[ty]], [e1], m)

let mkCallToSByteOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.sbyte_operator_info, [[ty]], [e1], m)

let mkCallToInt16Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int16_operator_info, [[ty]], [e1], m)

let mkCallToUInt16Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint16_operator_info, [[ty]], [e1], m)

let mkCallToInt32Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int32_operator_info, [[ty]], [e1], m)

let mkCallToUInt32Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint32_operator_info, [[ty]], [e1], m)

let mkCallToInt64Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.int64_operator_info, [[ty]], [e1], m)

let mkCallToUInt64Operator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.uint64_operator_info, [[ty]], [e1], m)

let mkCallToSingleOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.float32_operator_info, [[ty]], [e1], m)

let mkCallToDoubleOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.float_operator_info, [[ty]], [e1], m)

let mkCallToIntPtrOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.nativeint_operator_info, [[ty]], [e1], m)

let mkCallToUIntPtrOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.unativeint_operator_info, [[ty]], [e1], m)

let mkCallToCharOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.char_operator_info, [[ty]], [e1], m)

let mkCallToEnumOperator (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.enum_operator_info, [[ty]], [e1], m)

let mkCallArrayLength (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.array_length_info, [[ty]], [e1], m)

let mkCallArrayGet (g: TcGlobals) m ty e1 idx1 = mkApps g (typedExprForIntrinsic g m g.array_get_info, [[ty]], [ e1 ; idx1 ], m)

let mkCallArray2DGet (g: TcGlobals) m ty e1 idx1 idx2 = mkApps g (typedExprForIntrinsic g m g.array2D_get_info, [[ty]], [ e1 ; idx1; idx2 ], m)

let mkCallArray3DGet (g: TcGlobals) m ty e1 idx1 idx2 idx3 = mkApps g (typedExprForIntrinsic g m g.array3D_get_info, [[ty]], [ e1 ; idx1; idx2; idx3 ], m)

let mkCallArray4DGet (g: TcGlobals) m ty e1 idx1 idx2 idx3 idx4 = mkApps g (typedExprForIntrinsic g m g.array4D_get_info, [[ty]], [ e1 ; idx1; idx2; idx3; idx4 ], m)

let mkCallArraySet (g: TcGlobals) m ty e1 idx1 v = mkApps g (typedExprForIntrinsic g m g.array_set_info, [[ty]], [ e1 ; idx1; v ], m)

let mkCallArray2DSet (g: TcGlobals) m ty e1 idx1 idx2 v = mkApps g (typedExprForIntrinsic g m g.array2D_set_info, [[ty]], [ e1 ; idx1; idx2; v ], m)

let mkCallArray3DSet (g: TcGlobals) m ty e1 idx1 idx2 idx3 v = mkApps g (typedExprForIntrinsic g m g.array3D_set_info, [[ty]], [ e1 ; idx1; idx2; idx3; v ], m)

let mkCallArray4DSet (g: TcGlobals) m ty e1 idx1 idx2 idx3 idx4 v = mkApps g (typedExprForIntrinsic g m g.array4D_set_info, [[ty]], [ e1 ; idx1; idx2; idx3; idx4; v ], m)

let mkCallHash (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.hash_info, [[ty]], [ e1 ], m)

let mkCallBox (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.box_info, [[ty]], [ e1 ], m)

let mkCallIsNull (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.isnull_info, [[ty]], [ e1 ], m)

let mkCallRaise (g: TcGlobals) m ty e1 = mkApps g (typedExprForIntrinsic g m g.raise_info, [[ty]], [ e1 ], m)

let mkCallNewDecimal (g: TcGlobals) m (e1, e2, e3, e4, e5) = mkApps g (typedExprForIntrinsic g m g.new_decimal_info, [], [ e1;e2;e3;e4;e5 ], m)

let mkCallNewFormat (g: TcGlobals) m aty bty cty dty ety formatStringExpr =
    mkApps g (typedExprForIntrinsic g m g.new_format_info, [[aty;bty;cty;dty;ety]], [ formatStringExpr ], m)

let tryMkCallBuiltInWitness (g: TcGlobals) traitInfo argExprs m =
    let info, tinst = g.MakeBuiltInWitnessInfo traitInfo
    let vref = ValRefForIntrinsic info
    match vref.TryDeref with
    | ValueSome v -> 
        let f = exprForValRef m vref
        mkApps g ((f, v.Type), [tinst], argExprs, m) |> Some
    | ValueNone -> 
        None

let tryMkCallCoreFunctionAsBuiltInWitness (g: TcGlobals) info tyargs argExprs m =
    let vref = ValRefForIntrinsic info
    match vref.TryDeref with
    | ValueSome v -> 
        let f = exprForValRef m vref
        mkApps g ((f, v.Type), [tyargs], argExprs, m) |> Some
    | ValueNone -> 
        None

let TryEliminateDesugaredConstants g m c = 
    match c with 
    | Const.Decimal d -> 
        match System.Decimal.GetBits d with 
        | [| lo;med;hi; signExp |] -> 
            let scale = (min (((signExp &&& 0xFF0000) >>> 16) &&& 0xFF) 28) |> byte
            let isNegative = (signExp &&& 0x80000000) <> 0
            Some(mkCallNewDecimal g m (mkInt g m lo, mkInt g m med, mkInt g m hi, mkBool g m isNegative, mkByte g m scale) )
        | _ -> failwith "unreachable"
    | _ -> 
        None

let mkSeqTy (g: TcGlobals) ty = mkWoNullAppTy g.seq_tcr [ty] 

let mkIEnumeratorTy (g: TcGlobals) ty = mkWoNullAppTy g.tcref_System_Collections_Generic_IEnumerator [ty] 

let mkCallSeqCollect g m alphaTy betaTy arg1 arg2 = 
    let enumty2 = try rangeOfFunTy g (tyOfExpr g arg1) with _ -> (* defensive programming *) (mkSeqTy g betaTy)
    mkApps g (typedExprForIntrinsic g m g.seq_collect_info, [[alphaTy;enumty2;betaTy]], [ arg1; arg2 ], m) 
                  
let mkCallSeqUsing g m resourceTy elemTy arg1 arg2 = 
    // We're instantiating val using : 'a -> ('a -> 'sb) -> seq<'b> when 'sb :> seq<'b> and 'a :> IDisposable 
    // We set 'sb -> range(typeof(arg2)) 
    let enumty = try rangeOfFunTy g (tyOfExpr g arg2) with _ -> (* defensive programming *) (mkSeqTy g elemTy)
    mkApps g (typedExprForIntrinsic g m g.seq_using_info, [[resourceTy;enumty;elemTy]], [ arg1; arg2 ], m) 
                  
let mkCallSeqDelay g m elemTy arg1 = 
    mkApps g (typedExprForIntrinsic g m g.seq_delay_info, [[elemTy]], [ arg1 ], m) 
                  
let mkCallSeqAppend g m elemTy arg1 arg2 = 
    mkApps g (typedExprForIntrinsic g m g.seq_append_info, [[elemTy]], [ arg1; arg2 ], m) 

let mkCallSeqGenerated g m elemTy arg1 arg2 = 
    mkApps g (typedExprForIntrinsic g m g.seq_generated_info, [[elemTy]], [ arg1; arg2 ], m) 
                       
let mkCallSeqFinally g m elemTy arg1 arg2 = 
    mkApps g (typedExprForIntrinsic g m g.seq_finally_info, [[elemTy]], [ arg1; arg2 ], m) 

let mkCallSeqTryWith g m elemTy origSeq exnFilter exnHandler = 
    mkApps g (typedExprForIntrinsic g m g.seq_trywith_info, [[elemTy]], [ origSeq; exnFilter; exnHandler ], m) 
                       
let mkCallSeqOfFunctions g m ty1 ty2 arg1 arg2 arg3 = 
    mkApps g (typedExprForIntrinsic g m g.seq_of_functions_info, [[ty1;ty2]], [ arg1; arg2; arg3 ], m) 
                  
let mkCallSeqToArray g m elemTy arg1 =  
    mkApps g (typedExprForIntrinsic g m g.seq_to_array_info, [[elemTy]], [ arg1 ], m) 
                  
let mkCallSeqToList g m elemTy arg1 = 
    mkApps g (typedExprForIntrinsic g m g.seq_to_list_info, [[elemTy]], [ arg1 ], m) 
                  
let mkCallSeqMap g m inpElemTy genElemTy arg1 arg2 = 
    mkApps g (typedExprForIntrinsic g m g.seq_map_info, [[inpElemTy;genElemTy]], [ arg1; arg2 ], m) 
                  
let mkCallSeqSingleton g m ty1 arg1 = 
    mkApps g (typedExprForIntrinsic g m g.seq_singleton_info, [[ty1]], [ arg1 ], m) 
                  
let mkCallSeqEmpty g m ty1 = 
    mkApps g (typedExprForIntrinsic g m g.seq_empty_info, [[ty1]], [ ], m) 
                 
let mkCall_sprintf (g: TcGlobals) m funcTy fmtExpr fillExprs = 
    mkApps g (typedExprForIntrinsic g m g.sprintf_info, [[funcTy]], fmtExpr::fillExprs , m) 
                 
let mkCallDeserializeQuotationFSharp20Plus g m e1 e2 e3 e4 = 
    let args = [ e1; e2; e3; e4 ]
    mkApps g (typedExprForIntrinsic g m g.deserialize_quoted_FSharp_20_plus_info, [], [ mkRefTupledNoTypes g m args ], m)

let mkCallDeserializeQuotationFSharp40Plus g m e1 e2 e3 e4 e5 = 
    let args = [ e1; e2; e3; e4; e5 ]
    mkApps g (typedExprForIntrinsic g m g.deserialize_quoted_FSharp_40_plus_info, [], [ mkRefTupledNoTypes g m args ], m)

let mkCallCastQuotation g m ty e1 = 
    mkApps g (typedExprForIntrinsic g m g.cast_quotation_info, [[ty]], [ e1 ], m)

let mkCallLiftValue (g: TcGlobals) m ty e1 = 
    mkApps g (typedExprForIntrinsic g m g.lift_value_info, [[ty]], [e1], m)

let mkCallLiftValueWithName (g: TcGlobals) m ty nm e1 = 
    let vref = ValRefForIntrinsic g.lift_value_with_name_info 
    // Use "Expr.ValueWithName" if it exists in FSharp.Core
    match vref.TryDeref with
    | ValueSome _ ->
        mkApps g (typedExprForIntrinsic g m g.lift_value_with_name_info, [[ty]], [mkRefTupledNoTypes g m [e1; mkString g m nm]], m)
    | ValueNone ->
        mkCallLiftValue g m ty e1

let mkCallLiftValueWithDefn g m qty e1 = 
    assert isQuotedExprTy g qty
    let ty = destQuotedExprTy g qty
    let vref = ValRefForIntrinsic g.lift_value_with_defn_info 
    // Use "Expr.WithValue" if it exists in FSharp.Core
    match vref.TryDeref with
    | ValueSome _ ->
        let copyOfExpr = copyExpr g ValCopyFlag.CloneAll e1
        let quoteOfCopyOfExpr = Expr.Quote (copyOfExpr, ref None, false, m, qty)
        mkApps g (typedExprForIntrinsic g m g.lift_value_with_defn_info, [[ty]], [mkRefTupledNoTypes g m [e1; quoteOfCopyOfExpr]], m)
    | ValueNone ->
        Expr.Quote (e1, ref None, false, m, qty)

let mkCallCheckThis g m ty e1 = 
    mkApps g (typedExprForIntrinsic g m g.check_this_info, [[ty]], [e1], m)

let mkCallFailInit g m = 
    mkApps g (typedExprForIntrinsic g m g.fail_init_info, [], [mkUnit g m], m)

let mkCallFailStaticInit g m = 
    mkApps g (typedExprForIntrinsic g m g.fail_static_init_info, [], [mkUnit g m], m)

let mkCallQuoteToLinqLambdaExpression g m ty e1 = 
    mkApps g (typedExprForIntrinsic g m g.quote_to_linq_lambda_info, [[ty]], [e1], m)

let mkOptionToNullable g m ty e1 = 
    mkApps g (typedExprForIntrinsic g m g.option_toNullable_info, [[ty]], [e1], m)

let mkOptionDefaultValue g m ty e1 e2 = 
    mkApps g (typedExprForIntrinsic g m g.option_defaultValue_info, [[ty]], [e1; e2], m)

let mkLazyDelayed g m ty f = mkApps g (typedExprForIntrinsic g m g.lazy_create_info, [[ty]], [ f ], m) 

let mkLazyForce g m ty e = mkApps g (typedExprForIntrinsic g m g.lazy_force_info, [[ty]], [ e; mkUnit g m ], m) 

let mkGetString g m e1 e2 = mkApps g (typedExprForIntrinsic g m g.getstring_info, [], [e1;e2], m)

let mkGetStringChar = mkGetString

let mkGetStringLength g m e =
    let mspec = mspec_String_Length g
    Expr.Op (TOp.ILCall (false, false, false, false, ValUseFlag.NormalValUse, true, false, mspec.MethodRef, [], [], [g.int32_ty]), [], [e], m)

let mkStaticCall_String_Concat2 g m arg1 arg2 =
    let mspec = mspec_String_Concat2 g
    Expr.Op (TOp.ILCall (false, false, false, false, ValUseFlag.NormalValUse, false, false, mspec.MethodRef, [], [], [g.string_ty]), [], [arg1; arg2], m)

let mkStaticCall_String_Concat3 g m arg1 arg2 arg3 =
    let mspec = mspec_String_Concat3 g
    Expr.Op (TOp.ILCall (false, false, false, false, ValUseFlag.NormalValUse, false, false, mspec.MethodRef, [], [], [g.string_ty]), [], [arg1; arg2; arg3], m)

let mkStaticCall_String_Concat4 g m arg1 arg2 arg3 arg4 =
    let mspec = mspec_String_Concat4 g
    Expr.Op (TOp.ILCall (false, false, false, false, ValUseFlag.NormalValUse, false, false, mspec.MethodRef, [], [], [g.string_ty]), [], [arg1; arg2; arg3; arg4], m)

let mkStaticCall_String_Concat_Array g m arg =
    let mspec = mspec_String_Concat_Array g
    Expr.Op (TOp.ILCall (false, false, false, false, ValUseFlag.NormalValUse, false, false, mspec.MethodRef, [], [], [g.string_ty]), [], [arg], m)

// Quotations can't contain any IL.
// As a result, we aim to get rid of all IL generation in the typechecker and pattern match
// compiler, or else train the quotation generator to understand the generated IL. 
// Hence each of the following are marked with places where they are generated.

// Generated by the optimizer and the encoding of 'for' loops     
let mkDecr (g: TcGlobals) m e = mkAsmExpr ([ AI_sub ], [], [e; mkOne g m], [g.int_ty], m)

let mkIncr (g: TcGlobals) m e = mkAsmExpr ([ AI_add ], [], [mkOne g m; e], [g.int_ty], m)

// Generated by the pattern match compiler and the optimizer for
//    1. array patterns
//    2. optimizations associated with getting 'for' loops into the shape expected by the JIT.
// 
// NOTE: The conv.i4 assumes that int_ty is int32. Note: ldlen returns native UNSIGNED int 
let mkLdlen (g: TcGlobals) m arre = mkAsmExpr ([ I_ldlen; (AI_conv DT_I4) ], [], [ arre ], [ g.int_ty ], m)

let mkLdelem (_g: TcGlobals) m ty arre idxe = mkAsmExpr ([ I_ldelem_any (ILArrayShape.SingleDimensional, mkILTyvarTy 0us) ], [ty], [ arre;idxe ], [ ty ], m)

// This is generated in equality/compare/hash augmentations and in the pattern match compiler.
// It is understood by the quotation processor and turned into "Equality" nodes.
//
// Note: this is IL assembly code, don't go inserting this in expressions which will be exposed via quotations
let mkILAsmCeq (g: TcGlobals) m e1 e2 = mkAsmExpr ([ AI_ceq ], [], [e1; e2], [g.bool_ty], m)

let mkILAsmClt (g: TcGlobals) m e1 e2 = mkAsmExpr ([ AI_clt ], [], [e1; e2], [g.bool_ty], m)

// This is generated in the initialization of the "ctorv" field in the typechecker's compilation of
// an implicit class construction.
let mkNull m ty = Expr.Const (Const.Zero, m, ty)

let mkThrow m ty e = mkAsmExpr ([ I_throw ], [], [e], [ty], m)

let destThrow = function
    | Expr.Op (TOp.ILAsm ([I_throw], [ty2]), [], [e], m) -> Some (m, ty2, e)
    | _ -> None

let isThrow x = Option.isSome (destThrow x)

// reraise - parsed as library call - internally represented as op form.
let mkReraiseLibCall (g: TcGlobals) ty m =
    let ve, vt = typedExprForIntrinsic g m g.reraise_info
    Expr.App (ve, vt, [ty], [mkUnit g m], m)

let mkReraise m returnTy = Expr.Op (TOp.Reraise, [returnTy], [], m) (* could suppress unitArg *)

//----------------------------------------------------------------------------
// CompilationMappingAttribute, SourceConstructFlags
//----------------------------------------------------------------------------

let tnameCompilationSourceNameAttr = FSharpLib.Core + ".CompilationSourceNameAttribute"
let tnameCompilationArgumentCountsAttr = FSharpLib.Core + ".CompilationArgumentCountsAttribute"
let tnameCompilationMappingAttr = FSharpLib.Core + ".CompilationMappingAttribute"
let tnameSourceConstructFlags = FSharpLib.Core + ".SourceConstructFlags"

let tref_CompilationArgumentCountsAttr (g: TcGlobals) = mkILTyRef (g.fslibCcu.ILScopeRef, tnameCompilationArgumentCountsAttr)
let tref_CompilationMappingAttr (g: TcGlobals) = mkILTyRef (g.fslibCcu.ILScopeRef, tnameCompilationMappingAttr)
let tref_CompilationSourceNameAttr (g: TcGlobals) = mkILTyRef (g.fslibCcu.ILScopeRef, tnameCompilationSourceNameAttr)
let tref_SourceConstructFlags (g: TcGlobals) = mkILTyRef (g.fslibCcu.ILScopeRef, tnameSourceConstructFlags)

let mkCompilationMappingAttrPrim (g: TcGlobals) k nums = 
    mkILCustomAttribute (tref_CompilationMappingAttr g, 
                               ((mkILNonGenericValueTy (tref_SourceConstructFlags g)) :: (nums |> List.map (fun _ -> g.ilg.typ_Int32))), 
                               ((k :: nums) |> List.map (ILAttribElem.Int32)), 
                               [])

let mkCompilationMappingAttr g kind = mkCompilationMappingAttrPrim g kind []

let mkCompilationMappingAttrWithSeqNum g kind seqNum = mkCompilationMappingAttrPrim g kind [seqNum]

let mkCompilationMappingAttrWithVariantNumAndSeqNum g kind varNum seqNum = mkCompilationMappingAttrPrim g kind [varNum;seqNum]

let mkCompilationArgumentCountsAttr (g: TcGlobals) nums = 
    mkILCustomAttribute (tref_CompilationArgumentCountsAttr g, [ mkILArr1DTy g.ilg.typ_Int32 ], 
                               [ILAttribElem.Array (g.ilg.typ_Int32, List.map (ILAttribElem.Int32) nums)], 
                               [])

let mkCompilationSourceNameAttr (g: TcGlobals) n = 
    mkILCustomAttribute (tref_CompilationSourceNameAttr g, [ g.ilg.typ_String ], 
                               [ILAttribElem.String(Some n)], 
                               [])

let mkCompilationMappingAttrForQuotationResource (g: TcGlobals) (nm, tys: ILTypeRef list) = 
    mkILCustomAttribute (tref_CompilationMappingAttr g, 
                               [ g.ilg.typ_String; mkILArr1DTy g.ilg.typ_Type ], 
                               [ ILAttribElem.String (Some nm); ILAttribElem.Array (g.ilg.typ_Type, [ for ty in tys -> ILAttribElem.TypeRef (Some ty) ]) ], 
                               [])

//----------------------------------------------------------------------------
// Decode extensible typing attributes
//----------------------------------------------------------------------------

#if !NO_TYPEPROVIDERS

let isTypeProviderAssemblyAttr (cattr: ILAttribute) = 
    cattr.Method.DeclaringType.BasicQualifiedName = !! typeof<Microsoft.FSharp.Core.CompilerServices.TypeProviderAssemblyAttribute>.FullName

let TryDecodeTypeProviderAssemblyAttr (cattr: ILAttribute) : string MaybeNull option = 
    if isTypeProviderAssemblyAttr cattr then 
        let params_, _args = decodeILAttribData cattr 
        match params_ with // The first parameter to the attribute is the name of the assembly with the compiler extensions.
        | ILAttribElem.String (Some assemblyName) :: _ -> Some assemblyName
        | ILAttribElem.String None :: _ -> Some null
        | [] -> Some null
        | _ -> None
    else
        None

#endif

//----------------------------------------------------------------------------
// FSharpInterfaceDataVersionAttribute
//----------------------------------------------------------------------------

let tname_SignatureDataVersionAttr = FSharpLib.Core + ".FSharpInterfaceDataVersionAttribute"

let tref_SignatureDataVersionAttr fsharpCoreAssemblyScopeRef = mkILTyRef(fsharpCoreAssemblyScopeRef, tname_SignatureDataVersionAttr)

let mkSignatureDataVersionAttr (g: TcGlobals) (version: ILVersionInfo)  = 
    mkILCustomAttribute
        (tref_SignatureDataVersionAttr g.ilg.fsharpCoreAssemblyScopeRef, 
         [g.ilg.typ_Int32;g.ilg.typ_Int32;g.ilg.typ_Int32], 
         [ILAttribElem.Int32 (int32 version.Major)
          ILAttribElem.Int32 (int32 version.Minor) 
          ILAttribElem.Int32 (int32 version.Build)], [])

let tname_AutoOpenAttr = FSharpLib.Core + ".AutoOpenAttribute"

let IsSignatureDataVersionAttr cattr = isILAttribByName ([], tname_SignatureDataVersionAttr) cattr

let TryFindAutoOpenAttr cattr = 
    if isILAttribByName ([], tname_AutoOpenAttr) cattr then 
        match decodeILAttribData cattr with 
        | [ILAttribElem.String s], _ -> s
        | [], _ -> None
        | _ -> 
            warning(Failure(FSComp.SR.tastUnexpectedDecodeOfAutoOpenAttribute()))
            None
    else
        None
        
let TryFindInternalsVisibleToAttr cattr = 
    if isILAttribByName ([], tname_InternalsVisibleToAttribute) cattr then 
        match decodeILAttribData cattr with 
        | [ILAttribElem.String s], _ -> s
        | [], _ -> None
        | _ -> 
            warning(Failure(FSComp.SR.tastUnexpectedDecodeOfInternalsVisibleToAttribute()))
            None
    else
        None

let IsMatchingSignatureDataVersionAttr (version: ILVersionInfo) cattr = 
    IsSignatureDataVersionAttr cattr &&
    match decodeILAttribData cattr with 
    |  [ILAttribElem.Int32 u1; ILAttribElem.Int32 u2;ILAttribElem.Int32 u3 ], _ -> 
        (version.Major = uint16 u1) && (version.Minor = uint16 u2) && (version.Build = uint16 u3)
    | _ -> 
        warning(Failure(FSComp.SR.tastUnexpectedDecodeOfInterfaceDataVersionAttribute()))
        false

//--------------------------------------------------------------------------
// tupled lambda --> method/function with a given valReprInfo specification.
//
// AdjustArityOfLambdaBody: "(vs, body)" represents a lambda "fun (vs) -> body". The
// aim is to produce a "static method" represented by a pair
// "(mvs, body)" where mvs has the List.length "arity".
//--------------------------------------------------------------------------

let untupledToRefTupled g vs =
    let untupledTys = typesOfVals vs
    let m = (List.head vs).Range
    let tupledv, tuplede = mkCompGenLocal m "tupledArg" (mkRefTupledTy g untupledTys)
    let untupling_es = List.mapi (fun i _ -> mkTupleFieldGet g (tupInfoRef, tuplede, untupledTys, i, m)) untupledTys
    // These are non-sticky - at the caller,any sequence point for 'body' goes on 'body' _after_ the binding has been made
    tupledv, mkInvisibleLets m vs untupling_es 
    
// The required tupled-arity (arity) can either be 1 
// or N, and likewise for the tuple-arity of the input lambda, i.e. either 1 or N 
// where the N's will be identical. 
let AdjustArityOfLambdaBody g arity (vs: Val list) body = 
    let nvs = vs.Length
    if not (nvs = arity || nvs = 1 || arity = 1) then failwith "lengths don't add up"
    if arity = 0 then 
        vs, body
    elif nvs = arity then 
        vs, body
    elif nvs = 1 then
        let v = vs.Head
        let untupledTys = destRefTupleTy g v.Type
        if (untupledTys.Length <> arity) then failwith "length untupledTys <> arity"
        let dummyvs, dummyes = 
            untupledTys 
            |> List.mapi (fun i ty -> mkCompGenLocal v.Range (v.LogicalName + "_" + string i) ty) 
            |> List.unzip 
        let body = mkInvisibleLet v.Range v (mkRefTupled g v.Range dummyes untupledTys) body
        dummyvs, body
    else 
        let tupledv, untupler = untupledToRefTupled g vs
        [tupledv], untupler body

let MultiLambdaToTupledLambda g vs body = 
    match vs with 
    | [] -> failwith "MultiLambdaToTupledLambda: expected some arguments"
    | [v] -> v, body 
    | vs -> 
        let tupledv, untupler = untupledToRefTupled g vs
        tupledv, untupler body 

[<return: Struct>]
let (|RefTuple|_|) expr = 
    match expr with
    | Expr.Op (TOp.Tuple (TupInfo.Const false), _, args, _) -> ValueSome args
    | _ -> ValueNone

let MultiLambdaToTupledLambdaIfNeeded g (vs, arg) body = 
    match vs, arg with 
    | [], _ -> failwith "MultiLambdaToTupledLambda: expected some arguments"
    | [v], _ -> [(v, arg)], body 
    | vs, RefTuple args when args.Length = vs.Length -> List.zip vs args, body
    | vs, _ -> 
        let tupledv, untupler = untupledToRefTupled g vs
        [(tupledv, arg)], untupler body 

//--------------------------------------------------------------------------
// Beta reduction via let-bindings. Reduce immediate apps. of lambdas to let bindings. 
// Includes binding the immediate application of generic
// functions. Input type is the type of the function. Makes use of the invariant
// that any two expressions have distinct local variables (because we explicitly copy
// expressions).
//------------------------------------------------------------------------ 

let rec MakeApplicationAndBetaReduceAux g (f, fty, tyargsl: TType list list, argsl: Expr list, m) =
  match f with 
  | Expr.Let (bind, body, mLet, _) ->
      // Lift bindings out, i.e. (let x = e in f) y --> let x = e in f y 
      // This increases the scope of 'x', which I don't like as it mucks with debugging 
      // scopes of variables, but this is an important optimization, especially when the '|>' 
      // notation is used a lot. 
      mkLetBind mLet bind (MakeApplicationAndBetaReduceAux g (body, fty, tyargsl, argsl, m))
  | _ -> 
  match tyargsl with 
  | [] :: rest -> 
     MakeApplicationAndBetaReduceAux g (f, fty, rest, argsl, m)

  | tyargs :: rest -> 
      // Bind type parameters by immediate substitution 
      match f with 
      | Expr.TyLambda (_, tyvs, body, _, bodyTy) when tyvs.Length = List.length tyargs -> 
          let tpenv = bindTypars tyvs tyargs emptyTyparInst
          let body = instExpr g tpenv body
          let bodyTyR = instType tpenv bodyTy
          MakeApplicationAndBetaReduceAux g (body, bodyTyR, rest, argsl, m) 

      | _ -> 
          let f = mkAppsAux g f fty [tyargs] [] m
          let fty = applyTyArgs g fty tyargs 
          MakeApplicationAndBetaReduceAux g (f, fty, rest, argsl, m)
  | [] -> 
      match argsl with
      | _ :: _ ->
          // Bind term parameters by "let" explicit substitutions 
          // 
          // Only do this if there are enough lambdas for the number of arguments supplied. This is because
          // all arguments get evaluated before application.
          //
          // VALID:
          //      (fun a b -> E[a, b]) t1 t2 ---> let a = t1 in let b = t2 in E[t1, t2]
          // INVALID:
          //      (fun a -> E[a]) t1 t2 ---> let a = t1 in E[a] t2 UNLESS: E[a] has no effects OR t2 has no effects
          
          match tryStripLambdaN argsl.Length f with 
          | Some (argvsl, body) -> 
               assert (argvsl.Length = argsl.Length)
               let pairs, body = List.mapFoldBack (MultiLambdaToTupledLambdaIfNeeded g) (List.zip argvsl argsl) body
               let argvs2, args2 = List.unzip (List.concat pairs)
               mkLetsBind m (mkCompGenBinds argvs2 args2) body
          | _ -> 
              mkExprAppAux g f fty argsl m 

      | [] -> 
          f
      
let MakeApplicationAndBetaReduce g (f, fty, tyargsl, argl, m) = 
  MakeApplicationAndBetaReduceAux g (f, fty, tyargsl, argl, m)

[<return: Struct>]
let (|NewDelegateExpr|_|) g expr =
    match expr with
    | Expr.Obj (lambdaId, ty, a, b, [TObjExprMethod(c, d, e, tmvs, body, f)], [], m) when isDelegateTy g ty ->
        ValueSome (lambdaId, List.concat tmvs, body, m, (fun bodyR -> Expr.Obj (lambdaId, ty, a, b, [TObjExprMethod(c, d, e, tmvs, bodyR, f)], [], m)))
    | _ -> ValueNone

[<return: Struct>]
let (|DelegateInvokeExpr|_|) g expr =
    match expr with
    | Expr.App ((Expr.Val (invokeRef, _, _)) as delInvokeRef, delInvokeTy, tyargs, [delExpr;delInvokeArg], m) 
        when invokeRef.LogicalName = "Invoke" && isFSharpDelegateTy g (tyOfExpr g delExpr) -> 
            ValueSome(delInvokeRef, delInvokeTy, tyargs, delExpr, delInvokeArg, m)
    | _ -> ValueNone

[<return: Struct>]
let (|OpPipeRight|_|) g expr =
    match expr with
    | Expr.App (Expr.Val (vref, _, _), _, [_; resType], [xExpr; fExpr], m) 
        when valRefEq g vref g.piperight_vref -> 
            ValueSome(resType, xExpr, fExpr, m)
    | _ -> ValueNone

[<return: Struct>]
let (|OpPipeRight2|_|) g expr =
    match expr with
    | Expr.App (Expr.Val (vref, _, _), _, [_; _; resType], [Expr.Op (TOp.Tuple _, _, [arg1; arg2], _); fExpr], m) 
        when valRefEq g vref g.piperight2_vref -> 
            ValueSome(resType, arg1, arg2, fExpr, m)
    | _ -> ValueNone

[<return: Struct>]
let (|OpPipeRight3|_|) g expr =
    match expr with
    | Expr.App (Expr.Val (vref, _, _), _, [_; _; _; resType], [Expr.Op (TOp.Tuple _, _, [arg1; arg2; arg3], _); fExpr], m) 
        when valRefEq g vref g.piperight3_vref -> 
            ValueSome(resType, arg1, arg2, arg3, fExpr, m)
    | _ -> ValueNone

let rec MakeFSharpDelegateInvokeAndTryBetaReduce g (delInvokeRef, delExpr, delInvokeTy, tyargs, delInvokeArg, m) =
    match delExpr with 
    | Expr.Let (bind, body, mLet, _) ->
        mkLetBind mLet bind (MakeFSharpDelegateInvokeAndTryBetaReduce g (delInvokeRef, body, delInvokeTy, tyargs, delInvokeArg, m))
    | NewDelegateExpr g (_, argvs & _ :: _, body, m, _) ->
        let pairs, body = MultiLambdaToTupledLambdaIfNeeded g (argvs, delInvokeArg) body
        let argvs2, args2 = List.unzip pairs
        mkLetsBind m (mkCompGenBinds argvs2 args2) body
    | _ -> 
        // Remake the delegate invoke
        Expr.App (delInvokeRef, delInvokeTy, tyargs, [delExpr; delInvokeArg], m) 
      
//---------------------------------------------------------------------------
// Adjust for expected usage
// Convert a use of a value to saturate to the given arity.
//--------------------------------------------------------------------------- 

let MakeArgsForTopArgs _g m argTysl tpenv =
    argTysl |> List.mapi (fun i argTys -> 
        argTys |> List.mapi (fun j (argTy, argInfo: ArgReprInfo) -> 
            let ty = instType tpenv argTy
            let nm = 
               match argInfo.Name with 
               | None -> CompilerGeneratedName ("arg" + string i + string j)
               | Some id -> id.idText
            fst (mkCompGenLocal m nm ty)))

let AdjustValForExpectedValReprInfo g m (vref: ValRef) flags valReprInfo =

    let tps, argTysl, retTy, _ = GetValReprTypeInFSharpForm g valReprInfo vref.Type m
    let tpsR = copyTypars false tps
    let tyargsR = List.map mkTyparTy tpsR
    let tpenv = bindTypars tps tyargsR emptyTyparInst
    let rtyR = instType tpenv retTy
    let vsl = MakeArgsForTopArgs g m argTysl tpenv
    let call = MakeApplicationAndBetaReduce g (Expr.Val (vref, flags, m), vref.Type, [tyargsR], (List.map (mkRefTupledVars g m) vsl), m)
    let tauexpr, tauty = 
        List.foldBack 
            (fun vs (e, ty) -> mkMultiLambda m vs (e, ty), (mkFunTy g (mkRefTupledVarsTy g vs) ty))
            vsl
            (call, rtyR)
    // Build a type-lambda expression for the toplevel value if needed... 
    mkTypeLambda m tpsR (tauexpr, tauty), tpsR +-> tauty

let stripTupledFunTy g ty = 
    let argTys, retTy = stripFunTy g ty
    let curriedArgTys = argTys |> List.map (tryDestRefTupleTy g)
    curriedArgTys, retTy

[<return: Struct>]
let (|ExprValWithPossibleTypeInst|_|) expr =
    match expr with 
    | Expr.App (Expr.Val (vref, flags, m), _fty, tyargs, [], _) ->
        ValueSome (vref, flags, tyargs, m)
    | Expr.Val (vref, flags, m) ->
        ValueSome (vref, flags, [], m)
    | _ -> 
        ValueNone

let mkCoerceIfNeeded g tgtTy srcTy expr =
    if typeEquiv g tgtTy srcTy then 
        expr
    else 
        mkCoerceExpr(expr, tgtTy, expr.Range, srcTy)

let mkCompGenLetIn m nm ty e f = 
    let v, ve = mkCompGenLocal m nm ty
    mkCompGenLet m v e (f (v, ve))

let mkCompGenLetMutableIn m nm ty e f = 
    let v, ve = mkMutableCompGenLocal m nm ty
    mkCompGenLet m v e (f (v, ve))

/// Take a node representing a coercion from one function type to another, e.g.
///    A -> A * A -> int 
/// to 
///    B -> B * A -> int 
/// and return an expression of the correct type that doesn't use a coercion type. For example
/// return   
///    (fun b1 b2 -> E (b1 :> A) (b2 :> A))
///
///    - Use good names for the closure arguments if available
///    - Create lambda variables if needed, or use the supplied arguments if available.
///
/// Return the new expression and any unused suffix of supplied arguments
///
/// If E is a value with TopInfo then use the arity to help create a better closure.
/// In particular we can create a closure like this:
///    (fun b1 b2 -> E (b1 :> A) (b2 :> A))
/// rather than 
///    (fun b1 -> let clo = E (b1 :> A) in (fun b2 -> clo (b2 :> A)))
/// The latter closures are needed to carefully preserve side effect order
///
/// Note that the results of this translation are visible to quotations

let AdjustPossibleSubsumptionExpr g (expr: Expr) (suppliedArgs: Expr list) : (Expr* Expr list) option =

    match expr with 
    | Expr.Op (TOp.Coerce, [inputTy;actualTy], [exprWithActualTy], m) when 
        isFunTy g actualTy && isFunTy g inputTy ->
        
        if typeEquiv g actualTy inputTy then 
            Some(exprWithActualTy, suppliedArgs)
        else
            
            let curriedActualArgTys, retTy = stripTupledFunTy g actualTy

            let curriedInputTys, _ = stripFunTy g inputTy

            assert (curriedActualArgTys.Length = curriedInputTys.Length)

            let argTys = (curriedInputTys, curriedActualArgTys) ||> List.mapi2 (fun i x y -> (i, x, y))


            // Use the nice names for a function of known arity and name. Note that 'nice' here also 
            // carries a semantic meaning. For a function with top-info, 
            //   let f (x: A) (y: A) (z: A) = ...
            // we know there are no side effects on the application of 'f' to 1, 2 args. This greatly simplifies
            // the closure built for 
            //   f b1 b2 
            // and indeed for 
            //   f b1 b2 b3
            // we don't build any closure at all, and just return
            //   f (b1 :> A) (b2 :> A) (b3 :> A)
            
            let curriedNiceNames = 
                match stripExpr exprWithActualTy with 
                | ExprValWithPossibleTypeInst(vref, _, _, _) when vref.ValReprInfo.IsSome -> 

                    let _, argTysl, _, _ = GetValReprTypeInFSharpForm g vref.ValReprInfo.Value vref.Type expr.Range
                    argTysl |> List.mapi (fun i argTys -> 
                        argTys |> List.mapi (fun j (_, argInfo) -> 
                             match argInfo.Name with 
                             | None -> CompilerGeneratedName ("arg" + string i + string j)
                             | Some id -> id.idText))
                | _ -> 
                    []
             
            let nCurriedNiceNames = curriedNiceNames.Length 
            assert (curriedActualArgTys.Length >= nCurriedNiceNames)

            let argTysWithNiceNames, argTysWithoutNiceNames =
                List.splitAt nCurriedNiceNames argTys

            /// Only consume 'suppliedArgs' up to at most the number of nice arguments
            let nSuppliedArgs = min suppliedArgs.Length nCurriedNiceNames
            let suppliedArgs, droppedSuppliedArgs =
                List.splitAt nSuppliedArgs suppliedArgs

            /// The relevant range for any expressions and applications includes the arguments 
            let appm = (m, suppliedArgs) ||> List.fold (fun m e -> unionRanges m e.Range) 

            // See if we have 'enough' suppliedArgs. If not, we have to build some lambdas, and, 
            // we have to 'let' bind all arguments that we consume, e.g.
            //   Seq.take (effect;4) : int list -> int list
            // is a classic case. Here we generate
            //   let tmp = (effect;4) in 
            //   (fun v -> Seq.take tmp (v :> seq<_>))
            let buildingLambdas = nSuppliedArgs <> nCurriedNiceNames

            /// Given a tuple of argument variables that has a tuple type that satisfies the input argument types, 
            /// coerce it to a tuple that satisfies the matching coerced argument type(s).
            let CoerceDetupled (argTys: TType list) (detupledArgs: Expr list) (actualTys: TType list) =
                assert (actualTys.Length = argTys.Length)
                assert (actualTys.Length = detupledArgs.Length)
                // Inject the coercions into the user-supplied explicit tuple
                let argm = List.reduce unionRanges (detupledArgs |> List.map (fun e -> e.Range))
                mkRefTupled g argm (List.map3 (mkCoerceIfNeeded g) actualTys argTys detupledArgs) actualTys

            /// Given an argument variable of tuple type that has been evaluated and stored in the 
            /// given variable, where the tuple type that satisfies the input argument types, 
            /// coerce it to a tuple that satisfies the matching coerced argument type(s).
            let CoerceBoundTuple tupleVar argTys (actualTys: TType list) =
                assert (actualTys.Length > 1)
            
                mkRefTupled g appm 
                   ((actualTys, argTys) ||> List.mapi2 (fun i actualTy dummyTy ->  
                       let argExprElement = mkTupleFieldGet g (tupInfoRef, tupleVar, argTys, i, appm)
                       mkCoerceIfNeeded g actualTy dummyTy argExprElement))
                   actualTys

            /// Given an argument that has a tuple type that satisfies the input argument types, 
            /// coerce it to a tuple that satisfies the matching coerced argument type. Try to detuple the argument if possible.
            let CoerceTupled niceNames (argExpr: Expr) (actualTys: TType list) =
                let argExprTy = (tyOfExpr g argExpr)

                let argTys = 
                    match actualTys with 
                    | [_] -> 
                        [tyOfExpr g argExpr]
                    | _ -> 
                        tryDestRefTupleTy g argExprTy 
                
                assert (actualTys.Length = argTys.Length)
                let nm = match niceNames with [nm] -> nm | _ -> "arg"
                if buildingLambdas then 
                    // Evaluate the user-supplied tuple-valued argument expression, inject the coercions and build an explicit tuple
                    // Assign the argument to make sure it is only run once
                    //     f ~~>: B -> int
                    //     f ~~> : (B * B) -> int
                    //
                    //  for 
                    //     let f a = 1
                    //     let f (a, a) = 1
                    let v, ve = mkCompGenLocal appm nm argExprTy
                    let binderBuilder = (fun tm -> mkCompGenLet appm v argExpr tm)
                    let expr = 
                        match actualTys, argTys with
                        | [actualTy], [argTy] -> mkCoerceIfNeeded g actualTy argTy ve 
                        | _ -> CoerceBoundTuple ve argTys actualTys

                    binderBuilder, expr
                else                
                    if typeEquiv g (mkRefTupledTy g actualTys) argExprTy then 
                        id, argExpr
                    else
                    
                        let detupledArgs, argTys = 
                            match actualTys with 
                            | [_actualType] -> 
                                [argExpr], [tyOfExpr g argExpr]
                            | _ -> 
                                tryDestRefTupleExpr argExpr, tryDestRefTupleTy g argExprTy 

                        // OK, the tuples match, or there is no de-tupling, 
                        //     f x
                        //     f (x, y)
                        //
                        //  for 
                        //     let f (x, y) = 1
                        // and we're not building lambdas, just coerce the arguments in place
                        if detupledArgs.Length = actualTys.Length then 
                            id, CoerceDetupled argTys detupledArgs actualTys
                        else 
                            // In this case there is a tuple mismatch.
                            //     f p
                            //
                            //
                            //  for 
                            //     let f (x, y) = 1
                            // Assign the argument to make sure it is only run once
                            let v, ve = mkCompGenLocal appm nm argExprTy
                            let binderBuilder = (fun tm -> mkCompGenLet appm v argExpr tm)
                            let expr = CoerceBoundTuple ve argTys actualTys
                            binderBuilder, expr
                        

            // This variable is really a dummy to make the code below more regular. 
            // In the i = N - 1 cases we skip the introduction of the 'let' for
            // this variable.
            let resVar, resVarAsExpr = mkCompGenLocal appm "result" retTy
            let N = argTys.Length
            let cloVar, exprForOtherArgs, _ = 
                List.foldBack 
                    (fun (i, inpArgTy, actualArgTys) (cloVar: Val, res, resTy) -> 

                        let inpArgTys = 
                            match actualArgTys with 
                            | [_] -> [inpArgTy]
                            | _ -> destRefTupleTy g inpArgTy

                        assert (inpArgTys.Length = actualArgTys.Length)
                        
                        let inpsAsVars, inpsAsExprs = inpArgTys |> List.mapi (fun j ty -> mkCompGenLocal appm ("arg" + string i + string j) ty) |> List.unzip
                        let inpsAsActualArg = CoerceDetupled inpArgTys inpsAsExprs actualArgTys
                        let inpCloVarType = mkFunTy g (mkRefTupledTy g actualArgTys) cloVar.Type
                        let newResTy = mkFunTy g inpArgTy resTy
                        let inpCloVar, inpCloVarAsExpr = mkCompGenLocal appm ("clo" + string i) inpCloVarType
                        let newRes = 
                            // For the final arg we can skip introducing the dummy variable
                            if i = N - 1 then 
                                mkMultiLambda appm inpsAsVars 
                                    (mkApps g ((inpCloVarAsExpr, inpCloVarType), [], [inpsAsActualArg], appm), resTy)
                            else
                                mkMultiLambda appm inpsAsVars 
                                    (mkCompGenLet appm cloVar 
                                       (mkApps g ((inpCloVarAsExpr, inpCloVarType), [], [inpsAsActualArg], appm)) 
                                       res, 
                                     resTy)
                            
                        inpCloVar, newRes, newResTy)
                    argTysWithoutNiceNames
                    (resVar, resVarAsExpr, retTy)

            let exprForAllArgs =
                if isNil argTysWithNiceNames then 
                    mkCompGenLet appm cloVar exprWithActualTy exprForOtherArgs
                else
                    // Mark the up as Some/None
                    let suppliedArgs = List.map Some suppliedArgs @ List.replicate (nCurriedNiceNames - nSuppliedArgs) None

                    assert (suppliedArgs.Length = nCurriedNiceNames)

                    let lambdaBuilders, binderBuilders, inpsAsArgs = 
                    
                        (argTysWithNiceNames, curriedNiceNames, suppliedArgs) |||> List.map3 (fun (_, inpArgTy, actualArgTys) niceNames suppliedArg -> 

                                let inpArgTys = 
                                    match actualArgTys with 
                                    | [_] -> [inpArgTy]
                                    | _ -> destRefTupleTy g inpArgTy


                                /// Note: there might not be enough nice names, and they might not match in arity
                                let niceNames = 
                                    match niceNames with 
                                    | nms when nms.Length = inpArgTys.Length -> nms
                                    | [nm] -> inpArgTys |> List.mapi (fun i _ -> (nm + string i))
                                    | nms -> nms
                                match suppliedArg with 
                                | Some arg -> 
                                    let binderBuilder, inpsAsActualArg = CoerceTupled niceNames arg actualArgTys
                                    let lambdaBuilder = id
                                    lambdaBuilder, binderBuilder, inpsAsActualArg
                                | None -> 
                                    let inpsAsVars, inpsAsExprs = (niceNames, inpArgTys) ||> List.map2 (fun nm ty -> mkCompGenLocal appm nm ty) |> List.unzip
                                    let inpsAsActualArg = CoerceDetupled inpArgTys inpsAsExprs actualArgTys
                                    let lambdaBuilder = (fun tm -> mkMultiLambda appm inpsAsVars (tm, tyOfExpr g tm))
                                    let binderBuilder = id
                                    lambdaBuilder, binderBuilder, inpsAsActualArg)
                        |> List.unzip3
                    
                    // If no trailing args then we can skip introducing the dummy variable
                    // This corresponds to 
                    //    let f (x: A) = 1      
                    //
                    //   f ~~> type B -> int
                    //
                    // giving
                    //   (fun b -> f (b :> A))
                    // rather than 
                    //   (fun b -> let clo = f (b :> A) in clo)   
                    let exprApp = 
                        if isNil argTysWithoutNiceNames then 
                            mkApps g ((exprWithActualTy, actualTy), [], inpsAsArgs, appm)
                        else
                            mkCompGenLet appm 
                                    cloVar (mkApps g ((exprWithActualTy, actualTy), [], inpsAsArgs, appm)) 
                                    exprForOtherArgs

                    List.foldBack (fun f acc -> f acc) binderBuilders 
                        (List.foldBack (fun f acc -> f acc) lambdaBuilders exprApp)

            Some(exprForAllArgs, droppedSuppliedArgs)
    | _ -> 
        None
  
/// Find and make all subsumption eliminations 
let NormalizeAndAdjustPossibleSubsumptionExprs g inputExpr = 
    let expr, args = 
        // AdjustPossibleSubsumptionExpr can take into account an application
        match stripExpr inputExpr with 
        | Expr.App (f, _fty, [], args, _) ->
             f, args

        | _ -> 
            inputExpr, []
    
    match AdjustPossibleSubsumptionExpr g expr args with 
    | None -> 
        inputExpr
    | Some (exprR, []) -> 
        exprR
    | Some (exprR, argsR) -> 
        //printfn "adjusted...." 
        Expr.App (exprR, tyOfExpr g exprR, [], argsR, inputExpr.Range)  
             
  
//---------------------------------------------------------------------------
// LinearizeTopMatch - when only one non-failing target, make linear. The full
// complexity of this is only used for spectacularly rare bindings such as 
//    type ('a, 'b) either = This of 'a | That of 'b
//    let this_f1 = This (fun x -> x)
//    let This fA | That fA = this_f1
// 
// Here a polymorphic top level binding "fA" is _computed_ by a pattern match!!!
// The TAST coming out of type checking must, however, define fA as a type function, 
// since it is marked with an arity that indicates it's r.h.s. is a type function]
// without side effects and so can be compiled as a generic method (for example).

// polymorphic things bound in complex matches at top level require eta expansion of the 
// type function to ensure the r.h.s. of the binding is indeed a type function 
let etaExpandTypeLambda g m tps (tm, ty) = 
    if isNil tps then tm else mkTypeLambda m tps (mkApps g ((tm, ty), [(List.map mkTyparTy tps)], [], m), ty)

let AdjustValToHaveValReprInfo (tmp: Val) parent valData =
    tmp.SetValReprInfo (Some valData)
    tmp.SetDeclaringEntity parent
    tmp.SetIsMemberOrModuleBinding()

/// For match with only one non-failing target T0, the other targets, T1... failing (say, raise exception).
///   tree, T0(v0, .., vN) => rhs ; T1() => fail ; ...
/// Convert it to bind T0's variables, then continue with T0's rhs:
///   let tmp = switch tree, TO(fv0, ..., fvN) => Tup (fv0, ..., fvN) ; T1() => fail; ...
///   let v1 = #1 tmp in ...
///   and vN = #N tmp
///   rhs
/// Motivation:
/// - For top-level let bindings with possibly failing matches, 
///   this makes clear that subsequent bindings (if reached) are top-level ones.
let LinearizeTopMatchAux g parent (spBind, m, tree, targets, m2, ty) =
    let targetsL = Array.toList targets
    (* items* package up 0, 1, more items *)
    let itemsProj tys i x = 
        match tys with 
        | [] -> failwith "itemsProj: no items?"
        | [_] -> x (* no projection needed *)
        | tys -> Expr.Op (TOp.TupleFieldGet (tupInfoRef, i), tys, [x], m)
    let isThrowingTarget = function TTarget(_, x, _) -> isThrow x
    if 1 + List.count isThrowingTarget targetsL = targetsL.Length then
        // Have failing targets and ONE successful one, so linearize
        let (TTarget (vs, rhs, _)) = List.find (isThrowingTarget >> not) targetsL
        let fvs = vs |> List.map (fun v -> fst(mkLocal v.Range v.LogicalName v.Type)) (* fresh *)
        let vtys = vs |> List.map (fun v -> v.Type) 
        let tmpTy = mkRefTupledVarsTy g vs
        let tmp, tmpe = mkCompGenLocal m "matchResultHolder" tmpTy

        AdjustValToHaveValReprInfo tmp parent ValReprInfo.emptyValData

        let newTg = TTarget (fvs, mkRefTupledVars g m fvs, None)
        let fixup (TTarget (tvs, tx, flags)) = 
           match destThrow tx with
           | Some (m, _, e) -> 
               let tx = mkThrow m tmpTy e
               TTarget(tvs, tx, flags) (* Throwing targets, recast it's "return type" *)
           | None -> newTg (* Non-throwing target, replaced [new/old] *)
       
        let targets = Array.map fixup targets
        let binds = 
            vs |> List.mapi (fun i v -> 
                let ty = v.Type
                let rhs = etaExpandTypeLambda g m v.Typars (itemsProj vtys i tmpe, ty)
                // update the arity of the value 
                v.SetValReprInfo (Some (InferValReprInfoOfExpr g AllowTypeDirectedDetupling.Yes ty [] [] rhs))
                // This binding is deliberately non-sticky - any sequence point for 'rhs' goes on 'rhs' _after_ the binding has been evaluated
                mkInvisibleBind v rhs) in (* vi = proj tmp *)
        mkCompGenLet m
          tmp (primMkMatch (spBind, m, tree, targets, m2, tmpTy)) (* note, probably retyped match, but note, result still has same type *)
          (mkLetsFromBindings m binds rhs)                             
    else
        (* no change *)
        primMkMatch (spBind, m, tree, targets, m2, ty)

let LinearizeTopMatch g parent = function
  | Expr.Match (spBind, m, tree, targets, m2, ty) -> LinearizeTopMatchAux g parent (spBind, m, tree, targets, m2, ty)
  | x -> x


//---------------------------------------------------------------------------
// XmlDoc signatures
//---------------------------------------------------------------------------

let commaEncs strs = String.concat "," strs
let angleEnc str = "{" + str + "}" 
let ticksAndArgCountTextOfTyconRef (tcref: TyconRef) =
     // Generic type names are (name + "`" + digits) where name does not contain "`".
     let path = Array.toList (fullMangledPathToTyconRef tcref) @ [tcref.CompiledName]
     textOfPath path
     
let typarEnc _g (gtpsType, gtpsMethod) typar =
    match List.tryFindIndex (typarEq typar) gtpsType with
    | Some idx -> "`" + string idx // single-tick-index for typar from type
    | None ->
        match List.tryFindIndex (typarEq typar) gtpsMethod with
        | Some idx ->
            "``" + string idx // double-tick-index for typar from method
        | None ->
            warning(InternalError("Typar not found during XmlDoc generation", typar.Range))
            "``0"

let rec typeEnc g (gtpsType, gtpsMethod) ty =
    let stripped = stripTyEqnsAndMeasureEqns g ty
    match stripped with 
    | TType_forall _ -> 
        "Microsoft.FSharp.Core.FSharpTypeFunc"

    | _ when isByrefTy g ty -> 
        let ety = destByrefTy g ty
        typeEnc g (gtpsType, gtpsMethod) ety + "@"

    | _ when isNativePtrTy g ty -> 
        let ety = destNativePtrTy g ty
        typeEnc g (gtpsType, gtpsMethod) ety + "*"

    | TType_app (_, _, _nullness) when isArrayTy g ty -> 
        let tcref, tinst = destAppTy g ty        
        let rank = rankOfArrayTyconRef g tcref
        let arraySuffix = "[" + String.concat ", " (List.replicate (rank-1) "0:") + "]"
        typeEnc g (gtpsType, gtpsMethod) (List.head tinst) + arraySuffix

    | TType_ucase (_, tinst)   
    | TType_app (_, tinst, _) -> 
        let tyName = 
            let ty = stripTyEqnsAndMeasureEqns g ty
            match ty with
            | TType_app (tcref, _tinst, _nullness) -> 
                // Generic type names are (name + "`" + digits) where name does not contain "`".
                // In XML doc, when used in type instances, these do not use the ticks.
                let path = Array.toList (fullMangledPathToTyconRef tcref) @ [tcref.CompiledName]
                textOfPath (List.map DemangleGenericTypeName path)
            | _ ->
                assert false
                failwith "impossible"
        tyName + tyargsEnc g (gtpsType, gtpsMethod) tinst

    | TType_anon (anonInfo, tinst) -> 
        sprintf "%s%s" anonInfo.ILTypeRef.FullName (tyargsEnc g (gtpsType, gtpsMethod) tinst)

    | TType_tuple (tupInfo, tys) -> 
        if evalTupInfoIsStruct tupInfo then 
            sprintf "System.ValueTuple%s"(tyargsEnc g (gtpsType, gtpsMethod) tys)
        else 
            sprintf "System.Tuple%s"(tyargsEnc g (gtpsType, gtpsMethod) tys)

    | TType_fun (domainTy, rangeTy, _nullness) -> 
        "Microsoft.FSharp.Core.FSharpFunc" + tyargsEnc g (gtpsType, gtpsMethod) [domainTy; rangeTy]

    | TType_var (typar, _nullness) -> 
        typarEnc g (gtpsType, gtpsMethod) typar

    | TType_measure _ -> "?"

and tyargsEnc g (gtpsType, gtpsMethod) args = 
    match args with     
    | [] -> ""
    | [a] when (match (stripTyEqns g a) with TType_measure _ -> true | _ -> false) -> ""  // float<m> should appear as just "float" in the generated .XML xmldoc file
    | _ -> angleEnc (commaEncs (List.map (typeEnc g (gtpsType, gtpsMethod)) args)) 

let XmlDocArgsEnc g (gtpsType, gtpsMethod) argTys =
    if isNil argTys then "" 
    else "(" + String.concat "," (List.map (typeEnc g (gtpsType, gtpsMethod)) argTys) + ")"    

let buildAccessPath (cp: CompilationPath option) =
    match cp with
    | Some cp ->
        let ap = cp.AccessPath |> List.map fst |> List.toArray
        System.String.Join(".", ap)      
    | None -> "Extension Type"

let prependPath path name = if String.IsNullOrEmpty(path) then name else !!path + "." + name

let XmlDocSigOfVal g full path (v: Val) =
    let parentTypars, methTypars, cxs, argInfos, retTy, prefix, path, name = 

        // CLEANUP: this is one of several code paths that treat module values and members 
        // separately when really it would be cleaner to make sure GetValReprTypeInFSharpForm, GetMemberTypeInFSharpForm etc.
        // were lined up so code paths like this could be uniform
    
        match v.MemberInfo with 
        | Some membInfo when not v.IsExtensionMember -> 

            // Methods, Properties etc.
            let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
            let tps, witnessInfos, argInfos, retTy, _ = GetMemberTypeInMemberForm g membInfo.MemberFlags (Option.get v.ValReprInfo) numEnclosingTypars v.Type v.Range

            let prefix, name = 
                match membInfo.MemberFlags.MemberKind with 
                | SynMemberKind.ClassConstructor 
                | SynMemberKind.Constructor -> "M:", "#ctor"
                | SynMemberKind.Member -> "M:", v.CompiledName g.CompilerGlobalState
                | SynMemberKind.PropertyGetSet 
                | SynMemberKind.PropertySet
                | SynMemberKind.PropertyGet -> "P:", v.PropertyName

            let path = if v.HasDeclaringEntity then prependPath path v.DeclaringEntity.CompiledName else path

            let parentTypars, methTypars = 
                match PartitionValTypars g v with
                | Some(_, memberParentTypars, memberMethodTypars, _, _) -> memberParentTypars, memberMethodTypars
                | None -> [], tps

            parentTypars, methTypars, witnessInfos, argInfos, retTy, prefix, path, name

        | _ ->
            // Regular F# values and extension members 
            let w = arityOfVal v
            let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal v
            let tps, witnessInfos, argInfos, retTy, _ = GetValReprTypeInCompiledForm g w numEnclosingTypars v.Type v.Range
            let name = v.CompiledName g.CompilerGlobalState
            let prefix =
                if w.NumCurriedArgs = 0 && isNil tps then "P:"
                else "M:"
            [], tps, witnessInfos, argInfos, retTy, prefix, path, name

    let witnessArgTys = GenWitnessTys g cxs
    let argTys = argInfos |> List.concat |> List.map fst
    let argTys = witnessArgTys @ argTys @ (match retTy with Some t when full -> [t] | _ -> []) 
    let args = XmlDocArgsEnc g (parentTypars, methTypars) argTys
    let arity = List.length methTypars
    let genArity = if arity=0 then "" else sprintf "``%d" arity
    prefix + prependPath path name + genArity + args
  
let BuildXmlDocSig prefix path = prefix + List.fold prependPath "" path

// Would like to use "U:", but ParseMemberSignature only accepts C# signatures
let XmlDocSigOfUnionCase path = BuildXmlDocSig "T:" path

let XmlDocSigOfField path = BuildXmlDocSig "F:" path

let XmlDocSigOfProperty path = BuildXmlDocSig "P:" path

let XmlDocSigOfTycon path = BuildXmlDocSig "T:" path

let XmlDocSigOfSubModul path = BuildXmlDocSig "T:" path

let XmlDocSigOfEntity (eref: EntityRef) =
    XmlDocSigOfTycon [(buildAccessPath eref.CompilationPathOpt); eref.Deref.CompiledName]

//--------------------------------------------------------------------------
// Some unions have null as representations 
//--------------------------------------------------------------------------


let enum_CompilationRepresentationAttribute_Static = 0b0000000000000001
let enum_CompilationRepresentationAttribute_Instance = 0b0000000000000010
let enum_CompilationRepresentationAttribute_ModuleSuffix = 0b0000000000000100
let enum_CompilationRepresentationAttribute_PermitNull = 0b0000000000001000

let HasUseNullAsTrueValueAttribute g attribs =
     match TryFindFSharpInt32Attribute g g.attrib_CompilationRepresentationAttribute attribs with
     | Some flags -> ((flags &&& enum_CompilationRepresentationAttribute_PermitNull) <> 0)
     | _ -> false 

let TyconHasUseNullAsTrueValueAttribute g (tycon: Tycon) = HasUseNullAsTrueValueAttribute g tycon.Attribs 

// WARNING: this must match optimizeAlternativeToNull in ilx/cu_erase.fs
let CanHaveUseNullAsTrueValueAttribute (_g: TcGlobals) (tycon: Tycon) =
  (tycon.IsUnionTycon && 
   let ucs = tycon.UnionCasesArray
   (ucs.Length = 0 ||
     (ucs |> Array.existsOne (fun uc -> uc.IsNullary) &&
      ucs |> Array.exists (fun uc -> not uc.IsNullary))))

// WARNING: this must match optimizeAlternativeToNull in ilx/cu_erase.fs
let IsUnionTypeWithNullAsTrueValue (g: TcGlobals) (tycon: Tycon) =
  (tycon.IsUnionTycon && 
   let ucs = tycon.UnionCasesArray
   (ucs.Length = 0 ||
     (TyconHasUseNullAsTrueValueAttribute g tycon &&
      ucs |> Array.existsOne (fun uc -> uc.IsNullary) &&
      ucs |> Array.exists (fun uc -> not uc.IsNullary))))

let TyconCompilesInstanceMembersAsStatic g tycon = IsUnionTypeWithNullAsTrueValue g tycon
let TcrefCompilesInstanceMembersAsStatic g (tcref: TyconRef) = TyconCompilesInstanceMembersAsStatic g tcref.Deref

let inline HasConstraint ([<InlineIfLambda>] predicate) (tp:Typar)  = 
    tp.Constraints |> List.exists predicate

let inline tryGetTyparTyWithConstraint g ([<InlineIfLambda>] predicate) ty = 
    match tryDestTyparTy g ty with 
    | ValueSome tp as x when HasConstraint predicate tp -> x
    | _ -> ValueNone

let inline IsTyparTyWithConstraint g ([<InlineIfLambda>] predicate) ty = 
    match tryDestTyparTy g ty with 
    | ValueSome tp -> HasConstraint predicate tp
    | ValueNone -> false

// Note, isStructTy does not include type parameters with the ': struct' constraint
// This predicate is used to detect those type parameters.
let IsNonNullableStructTyparTy g ty = ty |> IsTyparTyWithConstraint g _.IsIsNonNullableStruct

// Note, isRefTy does not include type parameters with the ': not struct' or ': null' constraints
// This predicate is used to detect those type parameters.
let IsReferenceTyparTy g ty = ty |> IsTyparTyWithConstraint g (fun tc -> tc.IsIsReferenceType || tc.IsSupportsNull)

let GetTyparTyIfSupportsNull g ty = ty |> tryGetTyparTyWithConstraint g _.IsSupportsNull

let TypeNullNever g ty = 
    let underlyingTy = stripTyEqnsAndMeasureEqns g ty
    isStructTy g underlyingTy ||
    isByrefTy g underlyingTy ||
    IsNonNullableStructTyparTy g ty

/// The pre-nullness logic about whether a type admits the use of 'null' as a value.
let TypeNullIsExtraValue g m ty = 
    if isILReferenceTy g ty || isDelegateTy g ty then
        match tryTcrefOfAppTy g ty with 
        | ValueSome tcref -> 
            // Putting AllowNullLiteralAttribute(false) on an IL or provided 
            // type means 'null' can't be used with that type, otherwise it can
            TryFindTyconRefBoolAttribute g m g.attrib_AllowNullLiteralAttribute tcref <> Some false 
        | _ -> 
            // In pre-nullness, other IL reference types (e.g. arrays) always support null
            true
    elif TypeNullNever g ty then 
        false
    else 
        // In F# 4.x, putting AllowNullLiteralAttribute(true) on an F# type means 'null' can be used with that type
        match tryTcrefOfAppTy g ty with 
        | ValueSome tcref -> TryFindTyconRefBoolAttribute g m g.attrib_AllowNullLiteralAttribute tcref = Some true
        | ValueNone -> 

        // Consider type parameters
        (GetTyparTyIfSupportsNull g ty).IsSome

// Any mention of a type with AllowNullLiteral(true) is considered to be with-null
let intrinsicNullnessOfTyconRef g (tcref: TyconRef) =
    match TryFindTyconRefBoolAttribute g tcref.Range g.attrib_AllowNullLiteralAttribute tcref with
    | Some true -> g.knownWithNull
    | _ -> g.knownWithoutNull

let nullnessOfTy g ty =
    ty
    |> stripTyEqns g
    |> function
        | TType_app(tcref, _, nullness) ->
            let nullness2 = intrinsicNullnessOfTyconRef g tcref
            if nullness2 === g.knownWithoutNull then
                nullness
            else
                combineNullness nullness nullness2
        | TType_fun (_, _, nullness) | TType_var (_, nullness) ->
            nullness
        | _ -> g.knownWithoutNull

let changeWithNullReqTyToVariable g reqTy =
    let sty = stripTyEqns g reqTy
    match isTyparTy g sty with
    | false ->
        match nullnessOfTy g sty with
        | Nullness.Known NullnessInfo.AmbivalentToNull 
        | Nullness.Known NullnessInfo.WithNull when g.checkNullness -> 
            reqTy |> replaceNullnessOfTy (NewNullnessVar())
        | _ -> reqTy
    | true -> reqTy

/// When calling a null-allowing API, we prefer to infer a without null argument for idiomatic F# code.
/// That is, unless caller explicitly marks a value (e.g. coming from a function parameter) as WithNull, it should not be inferred as such.
let reqTyForArgumentNullnessInference g actualTy reqTy =
    // Only change reqd nullness if actualTy is an inference variable
    match tryDestTyparTy g actualTy with
    | ValueSome t when t.IsCompilerGenerated && not(t |> HasConstraint _.IsSupportsNull) ->
        changeWithNullReqTyToVariable g reqTy       
    | _ -> reqTy


let GetDisallowedNullness (g:TcGlobals) (ty:TType) =
    if g.checkNullness then
        let rec hasWithNullAnyWhere ty alreadyWrappedInOuterWithNull = 
            match ty with
            | TType_var (tp, n) -> 
                let withNull = alreadyWrappedInOuterWithNull || n.TryEvaluate() = (ValueSome NullnessInfo.WithNull)
                match tp.Solution with
                | None -> []
                | Some t -> hasWithNullAnyWhere t withNull

            | TType_app (tcr, tinst, _) -> 
                let tyArgs = tinst |> List.collect (fun t -> hasWithNullAnyWhere t false)
                
                match alreadyWrappedInOuterWithNull, tcr.TypeAbbrev with
                | true, _ when isStructTyconRef tcr -> ty :: tyArgs
                | true, _ when tcr.IsMeasureableReprTycon -> 
                    match tcr.TypeReprInfo with
                    | TMeasureableRepr realType ->
                        if hasWithNullAnyWhere realType true |> List.isEmpty then
                            []
                        else [ty]
                    | _ -> []
                | true, Some tAbbrev -> (hasWithNullAnyWhere tAbbrev true) @ tyArgs
                | _ -> tyArgs

            | TType_tuple (_,tupTypes) ->
                let inner = tupTypes |> List.collect (fun t -> hasWithNullAnyWhere t false)
                if alreadyWrappedInOuterWithNull then ty :: inner else inner

            | TType_anon (tys=tys) -> 
                let inner = tys |> List.collect (fun t -> hasWithNullAnyWhere t false)
                if alreadyWrappedInOuterWithNull then ty :: inner else inner
            | TType_fun (d, r, _) ->
                (hasWithNullAnyWhere d false) @ (hasWithNullAnyWhere r false)

            | TType_forall _ -> []
            | TType_ucase _ -> []
            | TType_measure m ->
                if alreadyWrappedInOuterWithNull then 
                    let measuresInside = 
                        ListMeasureVarOccs m 
                        |> List.choose (fun x -> x.Solution)
                        |> List.collect (fun x -> hasWithNullAnyWhere x true)
                    ty :: measuresInside
                else []

        hasWithNullAnyWhere ty false
    else
        []

let TypeHasAllowNull (tcref:TyconRef) g m =
    not tcref.IsStructOrEnumTycon &&
    not (isByrefLikeTyconRef g m tcref) && 
    (TryFindTyconRefBoolAttribute g m g.attrib_AllowNullLiteralAttribute tcref = Some true)

/// The new logic about whether a type admits the use of 'null' as a value.
let TypeNullIsExtraValueNew g m ty = 
    let sty = stripTyparEqns ty
    
    // Check if the type has AllowNullLiteral
    (match tryTcrefOfAppTy g sty with 
     | ValueSome tcref -> TypeHasAllowNull tcref g m
     | _ -> false) 
    ||
    // Check if the type has a nullness annotation
    (match (nullnessOfTy g sty).Evaluate() with 
     | NullnessInfo.AmbivalentToNull -> false
     | NullnessInfo.WithoutNull -> false
     | NullnessInfo.WithNull -> true)
    ||
    // Check if the type has a ': null' constraint
    (GetTyparTyIfSupportsNull g ty).IsSome

/// The pre-nullness logic about whether a type uses 'null' as a true representation value
let TypeNullIsTrueValue g ty =
    (match tryTcrefOfAppTy g ty with
     | ValueSome tcref -> IsUnionTypeWithNullAsTrueValue g tcref.Deref
     | _ -> false) 
    || isUnitTy g ty

/// Indicates if unbox<T>(null) is actively rejected at runtime.   See nullability RFC.  This applies to types that don't have null
/// as a valid runtime representation under old compatibility rules.
let TypeNullNotLiked g m ty = 
       not (TypeNullIsExtraValue g m ty) 
    && not (TypeNullIsTrueValue g ty) 
    && not (TypeNullNever g ty) 


let rec TypeHasDefaultValueAux isNew g m ty = 
    let ty = stripTyEqnsAndMeasureEqns g ty
    (if isNew then TypeNullIsExtraValueNew g m ty else TypeNullIsExtraValue g m ty)
    || (isStructTy g ty &&
        // Is it an F# struct type?
        (if isFSharpStructTy g ty then 
            let tcref, tinst = destAppTy g ty 
            let flds = 
                // Note this includes fields implied by the use of the implicit class construction syntax
                tcref.AllInstanceFieldsAsList
                  // We can ignore fields with the DefaultValue(false) attribute 
                  |> List.filter (fun fld -> TryFindFSharpBoolAttribute g g.attrib_DefaultValueAttribute fld.FieldAttribs <> Some false)

            flds |> List.forall (actualTyOfRecdField (mkTyconRefInst tcref tinst) >> TypeHasDefaultValueAux isNew g m)

         // Struct tuple types have a DefaultValue if all their element types have a default value
         elif isStructTupleTy g ty then 
            destStructTupleTy g ty |> List.forall (TypeHasDefaultValueAux isNew g m)
         
         // Struct anonymous record types have a DefaultValue if all their element types have a default value
         elif isStructAnonRecdTy g ty then 
            match tryDestAnonRecdTy g ty with
            | ValueNone -> true
            | ValueSome (_, ptys) -> ptys |> List.forall (TypeHasDefaultValueAux isNew g m)
         else
            // All nominal struct types defined in other .NET languages have a DefaultValue regardless of their instantiation
            true))
    || 
      // Check for type variables with the ":struct" and "(new : unit -> 'T)" constraints
      ( match ty |> tryGetTyparTyWithConstraint g _.IsIsNonNullableStruct with
        | ValueSome tp -> tp |> HasConstraint _.IsRequiresDefaultConstructor
        | ValueNone -> false)        

let TypeHasDefaultValue (g: TcGlobals) m ty = TypeHasDefaultValueAux false g m ty  

let TypeHasDefaultValueNew g m ty = TypeHasDefaultValueAux true g m ty  

/// Determines types that are potentially known to satisfy the 'comparable' constraint and returns
/// a set of residual types that must also satisfy the constraint
[<return: Struct>]
let (|SpecialComparableHeadType|_|) g ty =           
    if isAnyTupleTy g ty then 
        let _tupInfo, elemTys = destAnyTupleTy g ty
        ValueSome elemTys 
    elif isAnonRecdTy g ty then 
        match tryDestAnonRecdTy g ty with
        | ValueNone -> ValueSome []
        | ValueSome (_anonInfo, elemTys) -> ValueSome elemTys 
    else
        match tryAppTy g ty with
        | ValueSome (tcref, tinst) ->
            if isArrayTyconRef g tcref ||
               tyconRefEq g tcref g.system_UIntPtr_tcref ||
               tyconRefEq g tcref g.system_IntPtr_tcref then
                 ValueSome tinst 
            else 
                ValueNone
        | _ ->
            ValueNone

[<return: Struct>]
let (|SpecialEquatableHeadType|_|) g ty = (|SpecialComparableHeadType|_|) g ty

[<return: Struct>]
let (|SpecialNotEquatableHeadType|_|) g ty = 
    if isFunTy g ty then ValueSome() else ValueNone

let (|TyparTy|NullableTypar|StructTy|NullTrueValue|NullableRefType|WithoutNullRefType|UnresolvedRefType|) (ty,g) = 
    let sty = ty |> stripTyEqns g
    if isTyparTy g sty then 
        if (nullnessOfTy g sty).TryEvaluate() = ValueSome NullnessInfo.WithNull then
            NullableTypar
        else
            TyparTy
    elif isStructTy g sty then 
        StructTy
    elif TypeNullIsTrueValue g sty then
        NullTrueValue
    else
        match (nullnessOfTy g sty).TryEvaluate() with
        | ValueSome NullnessInfo.WithNull -> NullableRefType
        | ValueSome NullnessInfo.WithoutNull -> WithoutNullRefType
        | _ -> UnresolvedRefType

// Can we use the fast helper for the 'LanguagePrimitives.IntrinsicFunctions.TypeTestGeneric'? 
let canUseTypeTestFast g ty = 
     not (isTyparTy g ty) && 
     not (TypeNullIsTrueValue g ty)

// Can we use the fast helper for the 'LanguagePrimitives.IntrinsicFunctions.UnboxGeneric'? 
let canUseUnboxFast (g:TcGlobals) m ty = 
     if g.checkNullness then
         match (ty,g) with
         | TyparTy | WithoutNullRefType | UnresolvedRefType  -> false
         | StructTy | NullTrueValue | NullableRefType | NullableTypar  -> true
     else
         not (isTyparTy g ty) && 
         not (TypeNullNotLiked g m ty)
     
//--------------------------------------------------------------------------
// Nullness tests and pokes 
//--------------------------------------------------------------------------

// Generates the logical equivalent of
// match inp with :? ty as v -> e2[v] | _ -> e3 
//
// No sequence point is generated for this expression form as this function is only
// used for compiler-generated code.
let mkIsInstConditional g m tgtTy vinputExpr v e2 e3 = 
    
    if canUseTypeTestFast g tgtTy && isRefTy g tgtTy then 

        let mbuilder = MatchBuilder(DebugPointAtBinding.NoneAtInvisible, m)
        let tg2 = mbuilder.AddResultTarget(e2)
        let tg3 = mbuilder.AddResultTarget(e3)
        let dtree = TDSwitch(exprForVal m v, [TCase(DecisionTreeTest.IsNull, tg3)], Some tg2, m)
        let expr = mbuilder.Close(dtree, m, tyOfExpr g e2)
        mkCompGenLet m v (mkIsInst tgtTy vinputExpr m) expr

    else
        let mbuilder = MatchBuilder(DebugPointAtBinding.NoneAtInvisible, m)
        let tg2 = TDSuccess([mkCallUnbox g m tgtTy vinputExpr], mbuilder.AddTarget(TTarget([v], e2, None)))
        let tg3 = mbuilder.AddResultTarget(e3)
        let dtree = TDSwitch(vinputExpr, [TCase(DecisionTreeTest.IsInst(tyOfExpr g vinputExpr, tgtTy), tg2)], Some tg3, m)
        let expr = mbuilder.Close(dtree, m, tyOfExpr g e2)
        expr

(* match inp with DU(_) -> true | _ -> false *)
let mkUnionCaseTest (g: TcGlobals) (e1, cref: UnionCaseRef, tinst, m) =
    let mbuilder = new MatchBuilder(DebugPointAtBinding.NoneAtInvisible, m)
    let tg2 = mbuilder.AddResultTarget(Expr.Const(Const.Bool true, m, g.bool_ty))
    let tg3 = mbuilder.AddResultTarget(Expr.Const(Const.Bool false, m, g.bool_ty))
    let dtree = TDSwitch(e1, [TCase(DecisionTreeTest.UnionCase(cref, tinst), tg2)], Some tg3, m)
    let expr = mbuilder.Close(dtree, m, g.bool_ty)
    expr

// Null tests are generated by
//    1. The compilation of array patterns in the pattern match compiler
//    2. The compilation of string patterns in the pattern match compiler
// Called for when creating compiled form of 'let fixed ...'.
//
// No sequence point is generated for this expression form as this function is only
// used for compiler-generated code.
let mkNullTest g m e1 e2 e3 =
    let mbuilder = MatchBuilder(DebugPointAtBinding.NoneAtInvisible, m)
    let tg2 = mbuilder.AddResultTarget(e2)
    let tg3 = mbuilder.AddResultTarget(e3)            
    let dtree = TDSwitch(e1, [TCase(DecisionTreeTest.IsNull, tg3)], Some tg2, m)
    let expr = mbuilder.Close(dtree, m, tyOfExpr g e2)
    expr         

let mkNonNullTest (g: TcGlobals) m e =
    mkAsmExpr ([ AI_ldnull ; AI_cgt_un ], [], [e], [g.bool_ty], m)

// No sequence point is generated for this expression form as this function is only
// used for compiler-generated code.
let mkNonNullCond g m ty e1 e2 e3 =
    mkCond DebugPointAtBinding.NoneAtInvisible m ty (mkNonNullTest g m e1) e2 e3

// No sequence point is generated for this expression form as this function is only
// used for compiler-generated code.
let mkIfThen (g: TcGlobals) m e1 e2 =
    mkCond DebugPointAtBinding.NoneAtInvisible m g.unit_ty e1 e2 (mkUnit g m)

let ModuleNameIsMangled g attrs =
    match TryFindFSharpInt32Attribute g g.attrib_CompilationRepresentationAttribute attrs with
    | Some flags -> ((flags &&& enum_CompilationRepresentationAttribute_ModuleSuffix) <> 0)
    | _ -> false 

let CompileAsEvent g attrs = HasFSharpAttribute g g.attrib_CLIEventAttribute attrs 

let MemberIsCompiledAsInstance g parent isExtensionMember (membInfo: ValMemberInfo) attrs =
    // All extension members are compiled as static members
    if isExtensionMember then
        false
    // Abstract slots, overrides and interface impls are all true to IsInstance
    elif membInfo.MemberFlags.IsDispatchSlot || membInfo.MemberFlags.IsOverrideOrExplicitImpl || not (isNil membInfo.ImplementedSlotSigs) then
        membInfo.MemberFlags.IsInstance
    else 
        // Otherwise check attributes to see if there is an explicit instance or explicit static flag
        let explicitInstance, explicitStatic = 
            match TryFindFSharpInt32Attribute g g.attrib_CompilationRepresentationAttribute attrs with
            | Some flags -> 
              ((flags &&& enum_CompilationRepresentationAttribute_Instance) <> 0), 
              ((flags &&& enum_CompilationRepresentationAttribute_Static) <> 0)
            | _ -> false, false
        explicitInstance ||
        (membInfo.MemberFlags.IsInstance &&
         not explicitStatic &&
         not (TcrefCompilesInstanceMembersAsStatic g parent))


let isSealedTy g ty =
    let ty = stripTyEqnsAndMeasureEqns g ty
    not (isRefTy g ty) ||
    isUnitTy g ty || 
    isArrayTy g ty || 

    match metadataOfTy g ty with 
#if !NO_TYPEPROVIDERS
    | ProvidedTypeMetadata st -> st.IsSealed
#endif
    | ILTypeMetadata (TILObjectReprData(_, _, td)) -> td.IsSealed
    | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata ->
       if (isFSharpInterfaceTy g ty || isFSharpClassTy g ty) then 
          let tcref = tcrefOfAppTy g ty
          TryFindFSharpBoolAttribute g g.attrib_SealedAttribute tcref.Attribs = Some true
       else 
          // All other F# types, array, byref, tuple types are sealed
          true
   
let isComInteropTy g ty =
    let tcref = tcrefOfAppTy g ty
    match g.attrib_ComImportAttribute with
    | None -> false
    | Some attr -> TryFindFSharpBoolAttribute g attr tcref.Attribs = Some true
  
let ValSpecIsCompiledAsInstance g (v: Val) =
    match v.MemberInfo with 
    | Some membInfo -> 
        // Note it doesn't matter if we pass 'v.DeclaringEntity' or 'v.MemberApparentEntity' here. 
        // These only differ if the value is an extension member, and in that case MemberIsCompiledAsInstance always returns 
        // false anyway 
        MemberIsCompiledAsInstance g v.MemberApparentEntity v.IsExtensionMember membInfo v.Attribs  
    | _ -> false

let ValRefIsCompiledAsInstanceMember g (vref: ValRef) = ValSpecIsCompiledAsInstance g vref.Deref


//---------------------------------------------------------------------------
// Crack information about an F# object model call
//---------------------------------------------------------------------------

let GetMemberCallInfo g (vref: ValRef, vFlags) = 
    match vref.MemberInfo with 
    | Some membInfo when not vref.IsExtensionMember -> 
      let numEnclTypeArgs = vref.MemberApparentEntity.TyparsNoRange.Length
      let virtualCall = 
          (membInfo.MemberFlags.IsOverrideOrExplicitImpl || 
           membInfo.MemberFlags.IsDispatchSlot) && 
          not membInfo.MemberFlags.IsFinal && 
          (match vFlags with VSlotDirectCall -> false | _ -> true)
      let isNewObj = (membInfo.MemberFlags.MemberKind = SynMemberKind.Constructor) && (match vFlags with NormalValUse -> true | _ -> false)
      let isSuperInit = (membInfo.MemberFlags.MemberKind = SynMemberKind.Constructor) && (match vFlags with CtorValUsedAsSuperInit -> true | _ -> false) 
      let isSelfInit = (membInfo.MemberFlags.MemberKind = SynMemberKind.Constructor) && (match vFlags with CtorValUsedAsSelfInit -> true | _ -> false) 
      let isCompiledAsInstance = ValRefIsCompiledAsInstanceMember g vref
      let takesInstanceArg = isCompiledAsInstance && not isNewObj
      let isPropGet = (membInfo.MemberFlags.MemberKind = SynMemberKind.PropertyGet) && (membInfo.MemberFlags.IsInstance = isCompiledAsInstance)
      let isPropSet = (membInfo.MemberFlags.MemberKind = SynMemberKind.PropertySet) && (membInfo.MemberFlags.IsInstance = isCompiledAsInstance)
      numEnclTypeArgs, virtualCall, isNewObj, isSuperInit, isSelfInit, takesInstanceArg, isPropGet, isPropSet
    | _ -> 
      0, false, false, false, false, false, false, false

//---------------------------------------------------------------------------
// Active pattern name helpers
//---------------------------------------------------------------------------

let TryGetActivePatternInfo (vref: ValRef) =  
    // First is an optimization to prevent calls to string routines
    let logicalName = vref.LogicalName
    if logicalName.Length = 0 || logicalName[0] <> '|' then 
       None 
    else 
       ActivePatternInfoOfValName vref.DisplayNameCoreMangled vref.Range

type ActivePatternElemRef with 
    member x.LogicalName = 
        let (APElemRef(_, vref, n, _)) = x
        match TryGetActivePatternInfo vref with
        | None -> error(InternalError("not an active pattern name", vref.Range))
        | Some apinfo -> 
            let nms = apinfo.ActiveTags
            if n < 0 || n >= List.length nms then error(InternalError("name_of_apref: index out of range for active pattern reference", vref.Range))
            List.item n nms

    member x.DisplayNameCore = x.LogicalName

    member x.DisplayName = x.LogicalName |> ConvertLogicalNameToDisplayName

let mkChoiceTyconRef (g: TcGlobals) m n = 
     match n with 
     | 0 | 1 -> error(InternalError("mkChoiceTyconRef", m))
     | 2 -> g.choice2_tcr
     | 3 -> g.choice3_tcr
     | 4 -> g.choice4_tcr
     | 5 -> g.choice5_tcr
     | 6 -> g.choice6_tcr
     | 7 -> g.choice7_tcr
     | _ -> error(Error(FSComp.SR.tastActivePatternsLimitedToSeven(), m))

let mkChoiceTy (g: TcGlobals) m tinst = 
     match List.length tinst with 
     | 0 -> g.unit_ty
     | 1 -> List.head tinst
     | length -> mkWoNullAppTy (mkChoiceTyconRef g m length) tinst

let mkChoiceCaseRef g m n i = 
     mkUnionCaseRef (mkChoiceTyconRef g m n) ("Choice"+string (i+1)+"Of"+string n)

type ActivePatternInfo with 

    member x.DisplayNameCoreByIdx idx = x.ActiveTags[idx]

    member x.DisplayNameByIdx idx = x.ActiveTags[idx] |> ConvertLogicalNameToDisplayName

    member apinfo.ResultType g m retTys retKind = 
        let choicety = mkChoiceTy g m retTys
        if apinfo.IsTotal then choicety 
        else
            match retKind with
            | ActivePatternReturnKind.RefTypeWrapper -> mkOptionTy g choicety
            | ActivePatternReturnKind.StructTypeWrapper -> mkValueOptionTy g choicety
            | ActivePatternReturnKind.Boolean -> g.bool_ty
    
    member apinfo.OverallType g m argTy retTys retKind = 
        mkFunTy g argTy (apinfo.ResultType g m retTys retKind)

//---------------------------------------------------------------------------
// Active pattern validation
//---------------------------------------------------------------------------
    
// check if an active pattern takes type parameters only bound by the return types, 
// not by their argument types.
let doesActivePatternHaveFreeTypars g (v: ValRef) =
    let vty = v.TauType
    let vtps = v.Typars |> Zset.ofList typarOrder
    if not (isFunTy g v.TauType) then
        errorR(Error(FSComp.SR.activePatternIdentIsNotFunctionTyped(v.LogicalName), v.Range))
    let argTys, resty = stripFunTy g vty
    let argtps, restps= (freeInTypes CollectTypars argTys).FreeTypars, (freeInType CollectTypars resty).FreeTypars        
    // Error if an active pattern is generic in type variables that only occur in the result Choice<_, ...>.
    // Note: The test restricts to v.Typars since typars from the closure are considered fixed.
    not (Zset.isEmpty (Zset.inter (Zset.diff restps argtps) vtps)) 

//---------------------------------------------------------------------------
// RewriteExpr: rewrite bottom up with interceptors 
//---------------------------------------------------------------------------

[<NoEquality; NoComparison>]
type ExprRewritingEnv = 
    { PreIntercept: ((Expr -> Expr) -> Expr -> Expr option) option
      PostTransform: Expr -> Expr option
      PreInterceptBinding: ((Expr -> Expr) -> Binding -> Binding option) option
      RewriteQuotations: bool
      StackGuard: StackGuard }    

let rec rewriteBind env bind = 
     match env.PreInterceptBinding with 
     | Some f -> 
         match f (RewriteExpr env) bind with 
         | Some res -> res
         | None -> rewriteBindStructure env bind
     | None -> rewriteBindStructure env bind
     
and rewriteBindStructure env (TBind(v, e, letSeqPtOpt)) = 
     TBind(v, RewriteExpr env e, letSeqPtOpt) 

and rewriteBinds env binds = List.map (rewriteBind env) binds

and RewriteExpr env expr =
    env.StackGuard.Guard <| fun () ->
    match expr with 
    | LinearOpExpr _ 
    | LinearMatchExpr _ 
    | Expr.Let _ 
    | Expr.Sequential _ 
    | Expr.DebugPoint _ ->
        rewriteLinearExpr env expr id
    | _ -> 
        let expr = 
            match preRewriteExpr env expr with 
            | Some expr -> expr
            | None -> rewriteExprStructure env expr
        postRewriteExpr env expr 

and preRewriteExpr env expr = 
     match env.PreIntercept with 
     | Some f -> f (RewriteExpr env) expr
     | None -> None 

and postRewriteExpr env expr = 
     match env.PostTransform expr with 
     | None -> expr 
     | Some expr2 -> expr2

and rewriteExprStructure env expr =  
  match expr with
  | Expr.Const _ 
  | Expr.Val _ -> expr

  | Expr.App (f0, f0ty, tyargs, args, m) -> 
      let f0R = RewriteExpr env f0
      let argsR = rewriteExprs env args
      if f0 === f0R && args === argsR then expr
      else Expr.App (f0R, f0ty, tyargs, argsR, m)

  | Expr.Quote (ast, dataCell, isFromQueryExpression, m, ty) -> 
      let data = 
          match dataCell.Value with
          | None -> None
          | Some (data1, data2) -> Some(map3Of4 (rewriteExprs env) data1, map3Of4 (rewriteExprs env) data2)
      Expr.Quote ((if env.RewriteQuotations then RewriteExpr env ast else ast), ref data, isFromQueryExpression, m, ty)

  | Expr.Obj (_, ty, basev, basecall, overrides, iimpls, m) -> 
      let overridesR = List.map (rewriteObjExprOverride env) overrides
      let basecallR = RewriteExpr env basecall
      let iimplsR = List.map (rewriteObjExprInterfaceImpl env) iimpls
      mkObjExpr(ty, basev, basecallR, overridesR, iimplsR, m)

  | Expr.Link eref -> 
      RewriteExpr env eref.Value

  | Expr.DebugPoint _ ->
      failwith "unreachable - linear debug point"

  | Expr.Op (c, tyargs, args, m) -> 
      let argsR = rewriteExprs env args
      if args === argsR then expr 
      else Expr.Op (c, tyargs, argsR, m)

  | Expr.Lambda (_lambdaId, ctorThisValOpt, baseValOpt, argvs, body, m, bodyTy) -> 
      let bodyR = RewriteExpr env body
      rebuildLambda m ctorThisValOpt baseValOpt argvs (bodyR, bodyTy)

  | Expr.TyLambda (_lambdaId, tps, body, m, bodyTy) -> 
      let bodyR = RewriteExpr env body
      mkTypeLambda m tps (bodyR, bodyTy)

  | Expr.Match (spBind, mExpr, dtree, targets, m, ty) -> 
      let dtreeR = RewriteDecisionTree env dtree
      let targetsR = rewriteTargets env targets
      mkAndSimplifyMatch spBind mExpr m ty dtreeR targetsR

  | Expr.LetRec (binds, e, m, _) ->
      let bindsR = rewriteBinds env binds
      let eR = RewriteExpr env e
      Expr.LetRec (bindsR, eR, m, Construct.NewFreeVarsCache())

  | Expr.Let _ -> failwith "unreachable - linear let"

  | Expr.Sequential _ -> failwith "unreachable - linear seq"

  | Expr.StaticOptimization (constraints, e2, e3, m) ->
      let e2R = RewriteExpr env e2
      let e3R = RewriteExpr env e3
      Expr.StaticOptimization (constraints, e2R, e3R, m)

  | Expr.TyChoose (a, b, m) -> 
      Expr.TyChoose (a, RewriteExpr env b, m)

  | Expr.WitnessArg (witnessInfo, m) ->
      Expr.WitnessArg (witnessInfo, m)

and rewriteLinearExpr env expr contf =
    // schedule a rewrite on the way back up by adding to the continuation 
    let contf = contf << postRewriteExpr env
    match preRewriteExpr env expr with 
    | Some expr -> contf expr
    | None -> 
        match expr with 
        | Expr.Let (bind, bodyExpr, m, _) ->  
            let bind = rewriteBind env bind
            // tailcall
            rewriteLinearExpr env bodyExpr (contf << (fun bodyExprR ->
                mkLetBind m bind bodyExprR))
        
        | Expr.Sequential (expr1, expr2, dir, m) ->
            let expr1R = RewriteExpr env expr1
            // tailcall
            rewriteLinearExpr env expr2 (contf << (fun expr2R ->
                if expr1 === expr1R && expr2 === expr2R then expr 
                else Expr.Sequential (expr1R, expr2R, dir, m)))
        
        | LinearOpExpr (op, tyargs, argsFront, argLast, m) -> 
            let argsFrontR = rewriteExprs env argsFront
            // tailcall
            rewriteLinearExpr env argLast (contf << (fun argLastR ->
                if argsFront === argsFrontR && argLast === argLastR then expr 
                else rebuildLinearOpExpr (op, tyargs, argsFrontR, argLastR, m)))

        | LinearMatchExpr (spBind, mExpr, dtree, tg1, expr2, m2, ty) ->
            let dtree = RewriteDecisionTree env dtree
            let tg1R = rewriteTarget env tg1
            // tailcall
            rewriteLinearExpr env expr2 (contf << (fun expr2R ->
                rebuildLinearMatchExpr (spBind, mExpr, dtree, tg1R, expr2R, m2, ty)))
      
        | Expr.DebugPoint (dpm, innerExpr) ->
            rewriteLinearExpr env innerExpr (contf << (fun innerExprR ->
                 Expr.DebugPoint (dpm, innerExprR)))

        | _ -> 
            // no longer linear, no tailcall
            contf (RewriteExpr env expr) 

and rewriteExprs env exprs = List.mapq (RewriteExpr env) exprs

and rewriteFlatExprs env exprs = List.mapq (RewriteExpr env) exprs

and RewriteDecisionTree env x =
  match x with 
  | TDSuccess (es, n) -> 
      let esR = rewriteFlatExprs env es
      if LanguagePrimitives.PhysicalEquality es esR then x 
      else TDSuccess(esR, n)

  | TDSwitch (e, cases, dflt, m) ->
      let eR = RewriteExpr env e
      let casesR = List.map (fun (TCase(discrim, e)) -> TCase(discrim, RewriteDecisionTree env e)) cases
      let dfltR = Option.map (RewriteDecisionTree env) dflt
      TDSwitch (eR, casesR, dfltR, m)

  | TDBind (bind, body) ->
      let bindR = rewriteBind env bind
      let bodyR = RewriteDecisionTree env body
      TDBind (bindR, bodyR)

and rewriteTarget env (TTarget(vs, e, flags)) =
    let eR = RewriteExpr env e
    TTarget(vs, eR, flags)

and rewriteTargets env targets =
    List.map (rewriteTarget env) (Array.toList targets)

and rewriteObjExprOverride env (TObjExprMethod(slotsig, attribs, tps, vs, e, m)) =
    TObjExprMethod(slotsig, attribs, tps, vs, RewriteExpr env e, m)

and rewriteObjExprInterfaceImpl env (ty, overrides) = 
    (ty, List.map (rewriteObjExprOverride env) overrides)
    
and rewriteModuleOrNamespaceContents env x = 
    match x with 
    | TMDefRec(isRec, opens, tycons, mbinds, m) -> TMDefRec(isRec, opens, tycons, rewriteModuleOrNamespaceBindings env mbinds, m)
    | TMDefLet(bind, m) -> TMDefLet(rewriteBind env bind, m)
    | TMDefDo(e, m) -> TMDefDo(RewriteExpr env e, m)
    | TMDefOpens _ -> x
    | TMDefs defs -> TMDefs(List.map (rewriteModuleOrNamespaceContents env) defs)

and rewriteModuleOrNamespaceBinding env x = 
    match x with 
    | ModuleOrNamespaceBinding.Binding bind ->
        ModuleOrNamespaceBinding.Binding (rewriteBind env bind)
    | ModuleOrNamespaceBinding.Module(nm, rhs) ->
        ModuleOrNamespaceBinding.Module(nm, rewriteModuleOrNamespaceContents env rhs)

and rewriteModuleOrNamespaceBindings env mbinds =
    List.map (rewriteModuleOrNamespaceBinding env) mbinds

and RewriteImplFile env implFile =
    let (CheckedImplFile (fragName, signature, contents, hasExplicitEntryPoint, isScript, anonRecdTypes, namedDebugPointsForInlinedCode)) = implFile
    let contentsR = rewriteModuleOrNamespaceContents env contents
    let implFileR = CheckedImplFile (fragName, signature, contentsR, hasExplicitEntryPoint, isScript, anonRecdTypes, namedDebugPointsForInlinedCode)
    implFileR

//--------------------------------------------------------------------------
// Build a Remap that converts all "local" references to "public" things 
// accessed via non local references.
//--------------------------------------------------------------------------

let MakeExportRemapping viewedCcu (mspec: ModuleOrNamespace) = 

    let accEntityRemap (entity: Entity) acc = 
        match tryRescopeEntity viewedCcu entity with 
        | ValueSome eref -> 
            addTyconRefRemap (mkLocalTyconRef entity) eref acc
        | _ -> 
            if entity.IsNamespace then 
                acc
            else
                error(InternalError("Unexpected entity without a pubpath when remapping assembly data", entity.Range))

    let accValRemap (vspec: Val) acc = 
        // The acc contains the entity remappings
        match tryRescopeVal viewedCcu acc vspec with 
        | ValueSome vref -> 
            {acc with valRemap=acc.valRemap.Add vspec vref }
        | _ -> 
            error(InternalError("Unexpected value without a pubpath when remapping assembly data", vspec.Range))

    let mty = mspec.ModuleOrNamespaceType
    let entities = allEntitiesOfModuleOrNamespaceTy mty
    let vs = allValsOfModuleOrNamespaceTy mty
    // Remap the entities first so we can correctly remap the types in the signatures of the ValLinkageFullKey's in the value references
    let acc = List.foldBack accEntityRemap entities Remap.Empty
    let allRemap = List.foldBack accValRemap vs acc
    allRemap

//--------------------------------------------------------------------------
// Apply a "local to nonlocal" renaming to a module type. This can't use
// remap_mspec since the remapping we want isn't to newly created nodes
// but rather to remap to the nonlocal references. This is deliberately 
// "breaking" the binding structure implicit in the module type, which is
// the whole point - one things are rewritten to use non local references then
// the elements can be copied at will, e.g. when inlining during optimization.
//------------------------------------------------------------------------ 


let rec remapEntityDataToNonLocal ctxt tmenv (d: Entity) = 
    let tpsR, tmenvinner = tmenvCopyRemapAndBindTypars (remapAttribs ctxt tmenv) tmenv (d.entity_typars.Force(d.entity_range))
    let typarsR = LazyWithContext.NotLazy tpsR
    let attribsR = d.entity_attribs |> remapAttribs ctxt tmenvinner
    let tyconReprR = d.entity_tycon_repr |> remapTyconRepr ctxt tmenvinner
    let tyconAbbrevR = d.TypeAbbrev |> Option.map (remapType tmenvinner)
    let tyconTcaugR = d.entity_tycon_tcaug |> remapTyconAug tmenvinner
    let modulContentsR = 
        MaybeLazy.Strict (d.entity_modul_type.Value
                          |> mapImmediateValsAndTycons (remapTyconToNonLocal ctxt tmenv) (remapValToNonLocal ctxt tmenv))
    let exnInfoR = d.ExceptionInfo |> remapTyconExnInfo ctxt tmenvinner
    { d with 
          entity_typars = typarsR
          entity_attribs = attribsR
          entity_tycon_repr = tyconReprR
          entity_tycon_tcaug = tyconTcaugR
          entity_modul_type = modulContentsR
          entity_opt_data =
            match d.entity_opt_data with
            | Some dd ->
                Some { dd with entity_tycon_abbrev = tyconAbbrevR; entity_exn_info = exnInfoR }
            | _ -> None }

and remapTyconToNonLocal ctxt tmenv x = 
    x |> Construct.NewModifiedTycon (remapEntityDataToNonLocal ctxt tmenv)  

and remapValToNonLocal ctxt tmenv inp = 
    // creates a new stamp
    inp |> Construct.NewModifiedVal (remapValData ctxt tmenv)

let ApplyExportRemappingToEntity g tmenv x =
    let ctxt = { g = g; stackGuard = StackGuard(RemapExprStackGuardDepth, "RemapExprStackGuardDepth") }
    remapTyconToNonLocal ctxt tmenv x

(* Which constraints actually get compiled to .NET constraints? *)
let isCompiledOrWitnessPassingConstraint (g: TcGlobals) cx = 
    match cx with 
      | TyparConstraint.SupportsNull _ // this implies the 'class' constraint
      | TyparConstraint.IsReferenceType _  // this is the 'class' constraint
      | TyparConstraint.IsNonNullableStruct _ 
      | TyparConstraint.IsReferenceType _
      | TyparConstraint.RequiresDefaultConstructor _
      | TyparConstraint.IsUnmanaged _ //  implies "struct" and also causes a modreq
      | TyparConstraint.CoercesTo _ -> true
      | TyparConstraint.MayResolveMember _ when g.langVersion.SupportsFeature LanguageFeature.WitnessPassing -> true
      | _ -> false
    
// Is a value a first-class polymorphic value with .NET constraints, or witness-passing constraints? 
// Used to turn off TLR and method splitting and do not compile to
// FSharpTypeFunc, but rather bake a "local type function" for each TyLambda abstraction.
let IsGenericValWithGenericConstraints g (v: Val) = 
    isForallTy g v.Type && 
    v.Type |> destForallTy g |> fst |> List.exists (fun tp -> HasConstraint (isCompiledOrWitnessPassingConstraint g) tp)

// Does a type support a given interface? 
type Entity with 
    member tycon.HasInterface g ty = 
        tycon.TypeContents.tcaug_interfaces |> List.exists (fun (x, _, _) -> typeEquiv g ty x)  

    // Does a type have an override matching the given name and argument types? 
    // Used to detect the presence of 'Equals' and 'GetHashCode' in type checking 
    member tycon.HasOverride g nm argTys = 
        tycon.TypeContents.tcaug_adhoc 
        |> NameMultiMap.find nm
        |> List.exists (fun vref -> 
            match vref.MemberInfo with 
            | None -> false 
            | Some membInfo ->
 
            let argInfos = ArgInfosOfMember g vref 
            match argInfos with
            | [argInfos] ->
                List.lengthsEqAndForall2 (typeEquiv g) (List.map fst argInfos) argTys &&  
                membInfo.MemberFlags.IsOverrideOrExplicitImpl
            | _ -> false) 

    member tycon.TryGetMember g nm argTys = 
        tycon.TypeContents.tcaug_adhoc 
        |> NameMultiMap.find nm
        |> List.tryFind (fun vref -> 
            match vref.MemberInfo with 
            | None -> false 
            | _ ->

            let argInfos = ArgInfosOfMember g vref
            match argInfos with
            | [argInfos] -> List.lengthsEqAndForall2 (typeEquiv g) (List.map fst argInfos) argTys
            | _ -> false) 
    
    member tycon.HasMember g nm argTys = (tycon.TryGetMember g nm argTys).IsSome

type EntityRef with 
    member tcref.HasInterface g ty = tcref.Deref.HasInterface g ty
    member tcref.HasOverride g nm argTys = tcref.Deref.HasOverride g nm argTys
    member tcref.HasMember g nm argTys = tcref.Deref.HasMember g nm argTys

let mkFastForLoop g (spFor, spTo, m, idv: Val, start, dir, finish, body) =
    let dir = if dir then FSharpForLoopUp else FSharpForLoopDown 
    mkIntegerForLoop g (spFor, spTo, idv, start, dir, finish, body, m)

/// Accessing a binding of the form "let x = 1" or "let x = e" for any "e" satisfying the predicate
/// below does not cause an initialization trigger, i.e. does not get compiled as a static field.
let IsSimpleSyntacticConstantExpr g inputExpr = 
    let rec checkExpr (vrefs: Set<Stamp>) x = 
        match stripExpr x with 
        | Expr.Op (TOp.Coerce, _, [arg], _) 
             -> checkExpr vrefs arg
        | UnopExpr g (vref, arg) 
             when (valRefEq g vref g.unchecked_unary_minus_vref ||
                   valRefEq g vref g.unchecked_unary_plus_vref ||
                   valRefEq g vref g.unchecked_unary_not_vref ||
                   valRefEq g vref g.bitwise_unary_not_vref ||
                   valRefEq g vref g.enum_vref)
             -> checkExpr vrefs arg
        // compare, =, <>, +, -, <, >, <=, >=, <<<, >>>, &&&, |||, ^^^
        | BinopExpr g (vref, arg1, arg2) 
             when (valRefEq g vref g.equals_operator_vref ||
                   valRefEq g vref g.compare_operator_vref ||
                   valRefEq g vref g.unchecked_addition_vref ||
                   valRefEq g vref g.less_than_operator_vref ||
                   valRefEq g vref g.less_than_or_equals_operator_vref ||
                   valRefEq g vref g.greater_than_operator_vref ||
                   valRefEq g vref g.greater_than_or_equals_operator_vref ||
                   valRefEq g vref g.not_equals_operator_vref ||
                   valRefEq g vref g.unchecked_addition_vref ||
                   valRefEq g vref g.unchecked_multiply_vref ||
                   valRefEq g vref g.unchecked_subtraction_vref ||
                   // Note: division and modulus can raise exceptions, so are not included
                   valRefEq g vref g.bitwise_shift_left_vref ||
                   valRefEq g vref g.bitwise_shift_right_vref ||
                   valRefEq g vref g.bitwise_xor_vref ||
                   valRefEq g vref g.bitwise_and_vref ||
                   valRefEq g vref g.bitwise_or_vref ||
                   valRefEq g vref g.exponentiation_vref) &&
                   (not (typeEquiv g (tyOfExpr g arg1) g.string_ty) && not (typeEquiv g (tyOfExpr g arg1) g.decimal_ty) )
                -> checkExpr vrefs arg1 && checkExpr vrefs arg2 
        | Expr.Val (vref, _, _) -> vref.Deref.IsCompiledAsStaticPropertyWithoutField || vrefs.Contains vref.Stamp
        | Expr.Match (_, _, dtree, targets, _, _) -> checkDecisionTree vrefs dtree && targets |> Array.forall (checkDecisionTreeTarget vrefs)
        | Expr.Let (b, e, _, _) -> checkExpr vrefs b.Expr && checkExpr (vrefs.Add b.Var.Stamp) e
        | Expr.DebugPoint (_, b) -> checkExpr vrefs b
        | Expr.TyChoose (_, b, _) -> checkExpr vrefs b
        // Detect standard constants 
        | Expr.Const _ 
        | Expr.Op (TOp.UnionCase _, _, [], _)         // Nullary union cases
        | UncheckedDefaultOfExpr g _ 
        | SizeOfExpr g _ 
        | TypeOfExpr g _ -> true
        | NameOfExpr g _ when g.langVersion.SupportsFeature LanguageFeature.NameOf -> true
        // All others are not simple constant expressions
        | _ -> false

    and checkDecisionTree vrefs x = 
        match x with 
        | TDSuccess (es, _n) -> es |> List.forall (checkExpr vrefs)
        | TDSwitch (e, cases, dflt, _m) ->
            checkExpr vrefs e &&
            cases |> List.forall (checkDecisionTreeCase vrefs) &&
            dflt |> Option.forall (checkDecisionTree vrefs)
        | TDBind (bind, body) ->
            checkExpr vrefs bind.Expr &&
            checkDecisionTree (vrefs.Add bind.Var.Stamp) body

    and checkDecisionTreeCase vrefs (TCase(discrim, dtree)) = 
        (match discrim with
         | DecisionTreeTest.Const _c -> true
         | _ -> false) && 
        checkDecisionTree vrefs dtree

    and checkDecisionTreeTarget vrefs (TTarget(vs, e, _)) = 
        let vrefs = ((vrefs, vs) ||> List.fold (fun s v -> s.Add v.Stamp)) 
        checkExpr vrefs e

    checkExpr Set.empty inputExpr    

let EvalArithShiftOp (opInt8, opInt16, opInt32, opInt64, opUInt8, opUInt16, opUInt32, opUInt64) (arg1: Expr) (arg2: Expr) =
    // At compile-time we check arithmetic
    let m = unionRanges arg1.Range arg2.Range
    try
        match arg1, arg2 with
        | Expr.Const (Const.Int32 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.Int32 (opInt32 x1 shift), m, ty)
        | Expr.Const (Const.SByte x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.SByte (opInt8 x1 shift), m, ty)
        | Expr.Const (Const.Int16 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.Int16 (opInt16 x1 shift), m, ty)
        | Expr.Const (Const.Int64 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.Int64 (opInt64 x1 shift), m, ty)
        | Expr.Const (Const.Byte x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.Byte (opUInt8 x1 shift), m, ty)
        | Expr.Const (Const.UInt16 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.UInt16 (opUInt16 x1 shift), m, ty)
        | Expr.Const (Const.UInt32 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.UInt32 (opUInt32 x1 shift), m, ty)
        | Expr.Const (Const.UInt64 x1, _, ty), Expr.Const (Const.Int32 shift, _, _) -> Expr.Const (Const.UInt64 (opUInt64 x1 shift), m, ty)
        | _ -> error (Error ( FSComp.SR.tastNotAConstantExpression(), m))
    with :? System.OverflowException -> error (Error ( FSComp.SR.tastConstantExpressionOverflow(), m))

let EvalArithUnOp (opInt8, opInt16, opInt32, opInt64, opUInt8, opUInt16, opUInt32, opUInt64, opSingle, opDouble) (arg1: Expr) =
    // At compile-time we check arithmetic
    let m = arg1.Range
    try
        match arg1 with
        | Expr.Const (Const.Int32 x1, _, ty) -> Expr.Const (Const.Int32 (opInt32 x1), m, ty)
        | Expr.Const (Const.SByte x1, _, ty) -> Expr.Const (Const.SByte (opInt8 x1), m, ty)
        | Expr.Const (Const.Int16 x1, _, ty) -> Expr.Const (Const.Int16 (opInt16 x1), m, ty)
        | Expr.Const (Const.Int64 x1, _, ty) -> Expr.Const (Const.Int64 (opInt64 x1), m, ty)
        | Expr.Const (Const.Byte x1, _, ty) -> Expr.Const (Const.Byte (opUInt8 x1), m, ty)
        | Expr.Const (Const.UInt16 x1, _, ty) -> Expr.Const (Const.UInt16 (opUInt16 x1), m, ty)
        | Expr.Const (Const.UInt32 x1, _, ty) -> Expr.Const (Const.UInt32 (opUInt32 x1), m, ty)
        | Expr.Const (Const.UInt64 x1, _, ty) -> Expr.Const (Const.UInt64 (opUInt64 x1), m, ty)
        | Expr.Const (Const.Single x1, _, ty) -> Expr.Const (Const.Single (opSingle x1), m, ty)
        | Expr.Const (Const.Double x1, _, ty) -> Expr.Const (Const.Double (opDouble x1), m, ty)
        | _ -> error (Error ( FSComp.SR.tastNotAConstantExpression(), m))
    with :? System.OverflowException -> error (Error ( FSComp.SR.tastConstantExpressionOverflow(), m))

let EvalArithBinOp (opInt8, opInt16, opInt32, opInt64, opUInt8, opUInt16, opUInt32, opUInt64, opSingle, opDouble, opDecimal) (arg1: Expr) (arg2: Expr) =
    // At compile-time we check arithmetic
    let m = unionRanges arg1.Range arg2.Range
    try
        match arg1, arg2 with
        | Expr.Const (Const.Int32 x1, _, ty), Expr.Const (Const.Int32 x2, _, _) -> Expr.Const (Const.Int32 (opInt32 x1 x2), m, ty)
        | Expr.Const (Const.SByte x1, _, ty), Expr.Const (Const.SByte x2, _, _) -> Expr.Const (Const.SByte (opInt8 x1 x2), m, ty)
        | Expr.Const (Const.Int16 x1, _, ty), Expr.Const (Const.Int16 x2, _, _) -> Expr.Const (Const.Int16 (opInt16 x1 x2), m, ty)
        | Expr.Const (Const.Int64 x1, _, ty), Expr.Const (Const.Int64 x2, _, _) -> Expr.Const (Const.Int64 (opInt64 x1 x2), m, ty)
        | Expr.Const (Const.Byte x1, _, ty), Expr.Const (Const.Byte x2, _, _) -> Expr.Const (Const.Byte (opUInt8 x1 x2), m, ty)
        | Expr.Const (Const.UInt16 x1, _, ty), Expr.Const (Const.UInt16 x2, _, _) -> Expr.Const (Const.UInt16 (opUInt16 x1 x2), m, ty)
        | Expr.Const (Const.UInt32 x1, _, ty), Expr.Const (Const.UInt32 x2, _, _) -> Expr.Const (Const.UInt32 (opUInt32 x1 x2), m, ty)
        | Expr.Const (Const.UInt64 x1, _, ty), Expr.Const (Const.UInt64 x2, _, _) -> Expr.Const (Const.UInt64 (opUInt64 x1 x2), m, ty)
        | Expr.Const (Const.Single x1, _, ty), Expr.Const (Const.Single x2, _, _) -> Expr.Const (Const.Single (opSingle x1 x2), m, ty)
        | Expr.Const (Const.Double x1, _, ty), Expr.Const (Const.Double x2, _, _) -> Expr.Const (Const.Double (opDouble x1 x2), m, ty)
        | Expr.Const (Const.Decimal x1, _, ty), Expr.Const (Const.Decimal x2, _, _) -> Expr.Const (Const.Decimal (opDecimal x1 x2), m, ty)
        | _ -> error (Error ( FSComp.SR.tastNotAConstantExpression(), m))
    with :? System.OverflowException -> error (Error ( FSComp.SR.tastConstantExpressionOverflow(), m))

// See also PostTypeCheckSemanticChecks.CheckAttribArgExpr, which must match this precisely
let rec EvalAttribArgExpr suppressLangFeatureCheck (g: TcGlobals) (x: Expr) = 
    let ignore (_x: 'a) = Unchecked.defaultof<'a>
    let ignore2 (_x: 'a) (_y: 'a) = Unchecked.defaultof<'a>

    let inline checkFeature() =
        if suppressLangFeatureCheck = SuppressLanguageFeatureCheck.No then
            checkLanguageFeatureAndRecover g.langVersion LanguageFeature.ArithmeticInLiterals x.Range

    match x with 

    // Detect standard constants 
    | Expr.Const (c, m, _) -> 
        match c with 
        | Const.Bool _ 
        | Const.Int32 _ 
        | Const.SByte _
        | Const.Int16 _
        | Const.Int32 _
        | Const.Int64 _  
        | Const.Byte _
        | Const.UInt16 _
        | Const.UInt32 _
        | Const.UInt64 _
        | Const.Double _
        | Const.Single _
        | Const.Char _
        | Const.Zero
        | Const.String _
        | Const.Decimal _ ->
            x
        | Const.IntPtr _ | Const.UIntPtr _ | Const.Unit ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), m))
            x

    | TypeOfExpr g _ -> x
    | TypeDefOfExpr g _ -> x
    | Expr.Op (TOp.Coerce, _, [arg], _) -> 
        EvalAttribArgExpr suppressLangFeatureCheck g arg
    | EnumExpr g arg1 -> 
        EvalAttribArgExpr suppressLangFeatureCheck g arg1
    // Detect bitwise or of attribute flags
    | AttribBitwiseOrExpr g (arg1, arg2) -> 
        let v1 = EvalAttribArgExpr suppressLangFeatureCheck g arg1

        match v1 with
        | IntegerConstExpr ->
            EvalArithBinOp ((|||), (|||), (|||), (|||), (|||), (|||), (|||), (|||), ignore2, ignore2, ignore2) v1 (EvalAttribArgExpr suppressLangFeatureCheck g arg2) 
        | _ ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | SpecificBinopExpr g g.unchecked_addition_vref (arg1, arg2) ->
        let v1, v2 = EvalAttribArgExpr suppressLangFeatureCheck g arg1, EvalAttribArgExpr suppressLangFeatureCheck g arg2

        match v1, v2 with
        | Expr.Const (Const.String x1, m, ty), Expr.Const (Const.String x2, _, _) ->
            Expr.Const (Const.String (x1 + x2), m, ty)
        | Expr.Const (Const.Char x1, m, ty), Expr.Const (Const.Char x2, _, _) ->
            checkFeature()
            Expr.Const (Const.Char (x1 + x2), m, ty)
        | _ ->
            checkFeature()
            EvalArithBinOp (Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+), Checked.(+)) v1 v2
    | SpecificBinopExpr g g.unchecked_subtraction_vref (arg1, arg2) ->
        checkFeature()
        let v1, v2 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1, EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2
        
        match v1, v2 with
        | Expr.Const (Const.Char x1, m, ty), Expr.Const (Const.Char x2, _, _) ->
            Expr.Const (Const.Char (x1 - x2), m, ty)
        | _ ->
            EvalArithBinOp (Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-), Checked.(-)) v1 v2
    | SpecificBinopExpr g g.unchecked_multiply_vref (arg1, arg2) ->
        checkFeature()
        EvalArithBinOp (Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*), Checked.(*)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
    | SpecificBinopExpr g g.unchecked_division_vref (arg1, arg2) ->
        checkFeature()
        EvalArithBinOp ((/), (/), (/), (/), (/), (/), (/), (/), (/), (/), (/)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
    | SpecificBinopExpr g g.unchecked_modulus_vref (arg1, arg2) ->
        checkFeature()
        EvalArithBinOp ((%), (%), (%), (%), (%), (%), (%), (%), (%), (%), (%)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
    | SpecificBinopExpr g g.bitwise_shift_left_vref (arg1, arg2) ->
        checkFeature()
        EvalArithShiftOp ((<<<), (<<<), (<<<), (<<<), (<<<), (<<<), (<<<), (<<<)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
    | SpecificBinopExpr g g.bitwise_shift_right_vref (arg1, arg2) ->
        checkFeature()
        EvalArithShiftOp ((>>>), (>>>), (>>>), (>>>), (>>>), (>>>), (>>>), (>>>)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
    | SpecificBinopExpr g g.bitwise_and_vref (arg1, arg2) ->
        checkFeature()
        let v1 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1

        match v1 with
        | IntegerConstExpr ->
            EvalArithBinOp ((&&&), (&&&), (&&&), (&&&), (&&&), (&&&), (&&&), (&&&), ignore2, ignore2, ignore2) v1 (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
        | _ ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | SpecificBinopExpr g g.bitwise_xor_vref (arg1, arg2) ->
        checkFeature()
        let v1 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1

        match v1 with
        | IntegerConstExpr ->
            EvalArithBinOp ((^^^), (^^^), (^^^), (^^^), (^^^), (^^^), (^^^), (^^^), ignore2, ignore2, ignore2) v1 (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
        | _ ->
            errorR (Error (FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | SpecificBinopExpr g g.exponentiation_vref (arg1, arg2) ->
        checkFeature()
        let v1 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1

        match v1 with
        | FloatConstExpr ->
            EvalArithBinOp (ignore2, ignore2, ignore2, ignore2, ignore2, ignore2, ignore2, ignore2, ( ** ), ( ** ), ignore2) v1 (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg2)
        | _ ->
            errorR (Error (FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | SpecificUnopExpr g g.bitwise_unary_not_vref arg1 ->
        checkFeature()
        let v1 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1
        
        match v1 with
        | IntegerConstExpr ->
            EvalArithUnOp ((~~~), (~~~), (~~~), (~~~), (~~~), (~~~), (~~~), (~~~), ignore, ignore) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1)
        | _ ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | SpecificUnopExpr g g.unchecked_unary_minus_vref arg1 ->
        checkFeature()
        let v1 = EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1

        match v1 with
        | SignedConstExpr ->
            EvalArithUnOp (Checked.(~-), Checked.(~-), Checked.(~-), Checked.(~-), ignore, ignore, ignore, ignore, Checked.(~-), Checked.(~-)) v1
        | _ ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), v1.Range))
            x
    | SpecificUnopExpr g g.unchecked_unary_plus_vref arg1 ->
        checkFeature()
        EvalArithUnOp ((~+), (~+), (~+), (~+), (~+), (~+), (~+), (~+), (~+), (~+)) (EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1)
    | SpecificUnopExpr g g.unchecked_unary_not_vref arg1 ->
        checkFeature()

        match EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g arg1 with
        | Expr.Const (Const.Bool value, m, ty) ->
            Expr.Const (Const.Bool (not value), m, ty)
        | expr ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), expr.Range))
            x
    // Detect logical operations on booleans, which are represented as a match expression
    | Expr.Match (decision = TDSwitch (input = input; cases = [ TCase (DecisionTreeTest.Const (Const.Bool test), TDSuccess ([], targetNum)) ]); targets = [| TTarget (_, t0, _); TTarget (_, t1, _) |]) ->
        checkFeature()

        match EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g (stripDebugPoints input) with
        | Expr.Const (Const.Bool value, _, _) ->
            let pass, fail =
                if targetNum = 0 then
                    t0, t1
                else
                    t1, t0

            if value = test then
                EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g (stripDebugPoints pass)
            else
                EvalAttribArgExpr SuppressLanguageFeatureCheck.Yes g (stripDebugPoints fail)
        | _ ->
            errorR (Error ( FSComp.SR.tastNotAConstantExpression(), x.Range))
            x
    | _ -> 
        errorR (Error ( FSComp.SR.tastNotAConstantExpression(), x.Range))
        x

and EvaledAttribExprEquality g e1 e2 = 
    match e1, e2 with 
    | Expr.Const (c1, _, _), Expr.Const (c2, _, _) -> c1 = c2
    | TypeOfExpr g ty1, TypeOfExpr g ty2 -> typeEquiv g ty1 ty2
    | TypeDefOfExpr g ty1, TypeDefOfExpr g ty2 -> typeEquiv g ty1 ty2
    | _ -> false

[<return: Struct>]
let (|ConstToILFieldInit|_|) c =
    match c with 
    | Const.SByte n -> ValueSome (ILFieldInit.Int8 n)
    | Const.Int16 n -> ValueSome (ILFieldInit.Int16 n)
    | Const.Int32 n -> ValueSome (ILFieldInit.Int32 n)
    | Const.Int64 n -> ValueSome (ILFieldInit.Int64 n)
    | Const.Byte n -> ValueSome (ILFieldInit.UInt8 n)
    | Const.UInt16 n -> ValueSome (ILFieldInit.UInt16 n)
    | Const.UInt32 n -> ValueSome (ILFieldInit.UInt32 n)
    | Const.UInt64 n -> ValueSome (ILFieldInit.UInt64 n)
    | Const.Bool n -> ValueSome (ILFieldInit.Bool n)
    | Const.Char n -> ValueSome (ILFieldInit.Char (uint16 n))
    | Const.Single n -> ValueSome (ILFieldInit.Single n)
    | Const.Double n -> ValueSome (ILFieldInit.Double n)
    | Const.String s -> ValueSome (ILFieldInit.String s)
    | Const.Zero -> ValueSome ILFieldInit.Null
    | _ -> ValueNone

let EvalLiteralExprOrAttribArg g x = 
    match x with 
    | Expr.Op (TOp.Coerce, _, [Expr.Op (TOp.Array, [elemTy], args, m)], _)
    | Expr.Op (TOp.Array, [elemTy], args, m) ->
        let args = args |> List.map (EvalAttribArgExpr SuppressLanguageFeatureCheck.No g) 
        Expr.Op (TOp.Array, [elemTy], args, m) 
    | _ -> 
        EvalAttribArgExpr SuppressLanguageFeatureCheck.No g x

// Take into account the fact that some "instance" members are compiled as static
// members when using CompilationRepresentation.Static, or any non-virtual instance members
// in a type that supports "null" as a true value. This is all members
// where ValRefIsCompiledAsInstanceMember is false but membInfo.MemberFlags.IsInstance 
// is true.
//
// This is the right abstraction for viewing member types, but the implementation
// below is a little ugly.
let GetTypeOfIntrinsicMemberInCompiledForm g (vref: ValRef) =
    assert (not vref.IsExtensionMember)
    let membInfo, valReprInfo = checkMemberValRef vref
    let tps, cxs, argInfos, retTy, retInfo = GetTypeOfMemberInMemberForm g vref
    let argInfos = 
        // Check if the thing is really an instance member compiled as a static member
        // If so, the object argument counts as a normal argument in the compiled form
        if membInfo.MemberFlags.IsInstance && not (ValRefIsCompiledAsInstanceMember g vref) then 
            let _, origArgInfos, _, _ = GetValReprTypeInFSharpForm g valReprInfo vref.Type vref.Range
            match origArgInfos with
            | [] -> 
                errorR(InternalError("value does not have a valid member type", vref.Range))
                argInfos
            | h :: _ -> h :: argInfos
        else argInfos
    tps, cxs, argInfos, retTy, retInfo


//--------------------------------------------------------------------------
// Tuple compilation (expressions)
//------------------------------------------------------------------------ 


let rec mkCompiledTuple g isStruct (argTys, args, m) = 
    let n = List.length argTys 
    if n <= 0 then failwith "mkCompiledTuple"
    elif n < maxTuple then (mkCompiledTupleTyconRef g isStruct n, argTys, args, m)
    else
        let argTysA, argTysB = List.splitAfter goodTupleFields argTys
        let argsA, argsB = List.splitAfter goodTupleFields args
        let ty8, v8 = 
            match argTysB, argsB with 
            | [ty8], [arg8] -> 
                match ty8 with
                // if it's already been nested or ended, pass it through
                | TType_app(tn, _, _) when (isCompiledTupleTyconRef g tn) ->
                    ty8, arg8
                | _ ->
                    let ty8enc = TType_app((if isStruct then g.struct_tuple1_tcr else g.ref_tuple1_tcr), [ty8], g.knownWithoutNull)
                    let v8enc = Expr.Op (TOp.Tuple (mkTupInfo isStruct), [ty8], [arg8], m) 
                    ty8enc, v8enc
            | _ -> 
                let a, b, c, d = mkCompiledTuple g isStruct (argTysB, argsB, m)
                let ty8plus = TType_app(a, b, g.knownWithoutNull)
                let v8plus = Expr.Op (TOp.Tuple (mkTupInfo isStruct), b, c, d)
                ty8plus, v8plus
        let argTysAB = argTysA @ [ty8] 
        (mkCompiledTupleTyconRef g isStruct (List.length argTysAB), argTysAB, argsA @ [v8], m)

let mkILMethodSpecForTupleItem (_g: TcGlobals) (ty: ILType) n = 
    mkILNonGenericInstanceMethSpecInTy(ty, (if n < goodTupleFields then "get_Item"+(n+1).ToString() else "get_Rest"), [], mkILTyvarTy (uint16 n))

let mkILFieldSpecForTupleItem (ty: ILType) n = 
    mkILFieldSpecInTy (ty, (if n < goodTupleFields then "Item"+(n+1).ToString() else "Rest"), mkILTyvarTy (uint16 n))

let mkGetTupleItemN g m n (ty: ILType) isStruct expr retTy =
    if isStruct then
        mkAsmExpr ([mkNormalLdfld (mkILFieldSpecForTupleItem ty n) ], [], [expr], [retTy], m)
    else
        mkAsmExpr ([mkNormalCall(mkILMethodSpecForTupleItem g ty n)], [], [expr], [retTy], m)

/// Match an Int32 constant expression
[<return: Struct>]
let (|Int32Expr|_|) expr = 
    match expr with 
    | Expr.Const (Const.Int32 n, _, _) -> ValueSome n
    | _ -> ValueNone 

/// Match a try-finally expression
[<return: Struct>]
let (|TryFinally|_|) expr = 
    match expr with 
    | Expr.Op (TOp.TryFinally _, [_resTy], [Expr.Lambda (_, _, _, [_], e1, _, _); Expr.Lambda (_, _, _, [_], e2, _, _)], _) -> ValueSome(e1, e2)
    | _ -> ValueNone
    
// detect ONLY the while loops that result from compiling 'for ... in ... do ...'
[<return: Struct>]
let (|WhileLoopForCompiledForEachExpr|_|) expr = 
    match expr with 
    | Expr.Op (TOp.While (spInWhile, WhileLoopForCompiledForEachExprMarker), _, [Expr.Lambda (_, _, _, [_], e1, _, _); Expr.Lambda (_, _, _, [_], e2, _, _)], m) ->
        ValueSome(spInWhile, e1, e2, m)
    | _ -> ValueNone
    
[<return: Struct>]
let (|Let|_|) expr = 
    match expr with 
    | Expr.Let (TBind(v, e1, sp), e2, _, _) -> ValueSome(v, e1, sp, e2)
    | _ -> ValueNone

[<return: Struct>]
let (|RangeInt32Step|_|) g expr = 
    match expr with 
    // detect 'n .. m' 
    | Expr.App (Expr.Val (vf, _, _), _, [tyarg], [startExpr;finishExpr], _)
         when valRefEq g vf g.range_op_vref && typeEquiv g tyarg g.int_ty -> ValueSome(startExpr, 1, finishExpr)
    
    // detect (RangeInt32 startExpr N finishExpr), the inlined/compiled form of 'n .. m' and 'n .. N .. m'
    | Expr.App (Expr.Val (vf, _, _), _, [], [startExpr; Int32Expr n; finishExpr], _)
         when valRefEq g vf g.range_int32_op_vref -> ValueSome(startExpr, n, finishExpr)

    | _ -> ValueNone

[<return: Struct>]
let (|GetEnumeratorCall|_|) expr =   
    match expr with   
    | Expr.Op (TOp.ILCall ( _, _, _, _, _, _, _, ilMethodRef, _, _, _), _, [Expr.Val (vref, _, _) | Expr.Op (_, _, [Expr.Val (vref, ValUseFlag.NormalValUse, _)], _) ], _) ->  
        if ilMethodRef.Name = "GetEnumerator" then ValueSome vref  
        else ValueNone  
    | _ -> ValueNone  

// This code matches exactly the output of TcForEachExpr
[<return: Struct>]
let (|CompiledForEachExpr|_|) g expr =   
    match expr with
    | Let (enumerableVar, enumerableExpr, spFor, 
           Let (enumeratorVar, GetEnumeratorCall enumerableVar2, _enumeratorBind, 
              TryFinally (WhileLoopForCompiledForEachExpr (spInWhile, _, (Let (elemVar, _, _, bodyExpr) as elemLet), _), _))) 
                 // Apply correctness conditions to ensure this really is a compiled for-each expression.
                 when valRefEq g (mkLocalValRef enumerableVar) enumerableVar2 &&
                      enumerableVar.IsCompilerGenerated &&
                      enumeratorVar.IsCompilerGenerated &&
                      (let fvs = (freeInExpr CollectLocals bodyExpr)
                       not (Zset.contains enumerableVar fvs.FreeLocals) && 
                       not (Zset.contains enumeratorVar fvs.FreeLocals)) ->

        // Extract useful ranges
        let mBody = bodyExpr.Range
        let mWholeExpr = expr.Range
        let mIn = elemLet.Range

        let mFor = match spFor with DebugPointAtBinding.Yes mFor -> mFor | _ -> enumerableExpr.Range
        let spIn, mIn = match spInWhile with DebugPointAtWhile.Yes mIn -> DebugPointAtInOrTo.Yes mIn, mIn | _ -> DebugPointAtInOrTo.No, mIn
        let spInWhile = match spIn with DebugPointAtInOrTo.Yes m -> DebugPointAtWhile.Yes m | DebugPointAtInOrTo.No -> DebugPointAtWhile.No
        let enumerableTy = tyOfExpr g enumerableExpr

        ValueSome (enumerableTy, enumerableExpr, elemVar, bodyExpr, (mBody, spFor, spIn, mFor, mIn, spInWhile, mWholeExpr))
    | _ -> ValueNone  

[<return: Struct>]
let (|CompiledInt32RangeForEachExpr|_|) g expr = 
    match expr with
    | CompiledForEachExpr g (_, RangeInt32Step g (startExpr, step, finishExpr), elemVar, bodyExpr, ranges) ->
        ValueSome (startExpr, step, finishExpr, elemVar, bodyExpr, ranges)
    | _ -> ValueNone

[<return: Struct>]
let (|ValApp|_|) g vref expr =
    match expr with
    | Expr.App (Expr.Val (vref2, _, _), _f0ty, tyargs, args, m) when valRefEq g vref vref2 -> ValueSome (tyargs, args, m)
    | _ -> ValueNone

[<RequireQualifiedAccess>]
module IntegralConst =
    /// Constant 0.
    [<return: Struct>]
    let (|Zero|_|) c =
        match c with
        | Const.Zero
        | Const.Int32 0
        | Const.Int64 0L
        | Const.UInt64 0UL
        | Const.UInt32 0u
        | Const.IntPtr 0L
        | Const.UIntPtr 0UL
        | Const.Int16 0s
        | Const.UInt16 0us
        | Const.SByte 0y
        | Const.Byte 0uy
        | Const.Char '\000' -> ValueSome Zero
        | _ -> ValueNone

    /// Constant 1.
    [<return: Struct>]
    let (|One|_|) expr =
        match expr with
        | Const.Int32 1
        | Const.Int64 1L
        | Const.UInt64 1UL
        | Const.UInt32 1u
        | Const.IntPtr 1L
        | Const.UIntPtr 1UL
        | Const.Int16 1s
        | Const.UInt16 1us
        | Const.SByte 1y
        | Const.Byte 1uy
        | Const.Char '\001' -> ValueSome One
        | _ -> ValueNone

    /// Constant -1.
    [<return: Struct>]
    let (|MinusOne|_|) c =
        match c with
        | Const.Int32 -1
        | Const.Int64 -1L
        | Const.IntPtr -1L
        | Const.Int16 -1s
        | Const.SByte -1y -> ValueSome MinusOne
        | _ -> ValueNone

    /// Positive constant.
    [<return: Struct>]
    let (|Positive|_|) c =
        match c with
        | Const.Int32 v when v > 0 -> ValueSome Positive
        | Const.Int64 v when v > 0L -> ValueSome Positive
        // sizeof<nativeint> is not constant, so |𝑐| ≥ 0x80000000n cannot be treated as a constant.
        | Const.IntPtr v when v > 0L && uint64 v < 0x80000000UL -> ValueSome Positive
        | Const.Int16 v when v > 0s -> ValueSome Positive
        | Const.SByte v when v > 0y -> ValueSome Positive
        | Const.UInt64 v when v > 0UL -> ValueSome Positive
        | Const.UInt32 v when v > 0u -> ValueSome Positive
        // sizeof<unativeint> is not constant, so |𝑐| > 0xffffffffun cannot be treated as a constant.
        | Const.UIntPtr v when v > 0UL && v <= 0xffffffffUL -> ValueSome Positive
        | Const.UInt16 v when v > 0us -> ValueSome Positive
        | Const.Byte v when v > 0uy -> ValueSome Positive
        | Const.Char v when v > '\000' -> ValueSome Positive
        | _ -> ValueNone

    /// Negative constant.
    [<return: Struct>]
    let (|Negative|_|) c =
        match c with
        | Const.Int32 v when v < 0 -> ValueSome Negative
        | Const.Int64 v when v < 0L -> ValueSome Negative
        // sizeof<nativeint> is not constant, so |𝑐| ≥ 0x80000000n cannot be treated as a constant.
        | Const.IntPtr v when v < 0L && uint64 v < 0x80000000UL -> ValueSome Negative
        | Const.Int16 v when v < 0s -> ValueSome Negative
        | Const.SByte v when v < 0y -> ValueSome Negative
        | _ -> ValueNone

    /// Returns the absolute value of the given integral constant.
    let abs c =
        match c with
        | Const.Int32 Int32.MinValue -> Const.UInt32 (uint Int32.MaxValue + 1u)
        | Const.Int64 Int64.MinValue -> Const.UInt64 (uint64 Int64.MaxValue + 1UL)
        | Const.IntPtr Int64.MinValue -> Const.UIntPtr (uint64 Int64.MaxValue + 1UL)
        | Const.Int16 Int16.MinValue -> Const.UInt16 (uint16 Int16.MaxValue + 1us)
        | Const.SByte SByte.MinValue -> Const.Byte (byte SByte.MaxValue + 1uy)
        | Const.Int32 v -> Const.Int32 (abs v)
        | Const.Int64 v -> Const.Int64 (abs v)
        | Const.IntPtr v -> Const.IntPtr (abs v)
        | Const.Int16 v -> Const.Int16 (abs v)
        | Const.SByte v -> Const.SByte (abs v)
        | _ -> c

/// start..finish
/// start..step..finish
[<return: Struct>]
let (|IntegralRange|_|) g expr =
    match expr with
    | ValApp g g.range_int32_op_vref ([], [start; step; finish], _) -> ValueSome (g.int32_ty, (start, step, finish))
    | ValApp g g.range_int64_op_vref ([], [start; step; finish], _) -> ValueSome (g.int64_ty, (start, step, finish))
    | ValApp g g.range_uint64_op_vref ([], [start; step; finish], _) -> ValueSome (g.uint64_ty, (start, step, finish))
    | ValApp g g.range_uint32_op_vref ([], [start; step; finish], _) -> ValueSome (g.uint32_ty, (start, step, finish))
    | ValApp g g.range_nativeint_op_vref ([], [start; step; finish], _) -> ValueSome (g.nativeint_ty, (start, step, finish))
    | ValApp g g.range_unativeint_op_vref ([], [start; step; finish], _) -> ValueSome (g.unativeint_ty, (start, step, finish))
    | ValApp g g.range_int16_op_vref ([], [start; step; finish], _) -> ValueSome (g.int16_ty, (start, step, finish))
    | ValApp g g.range_uint16_op_vref ([], [start; step; finish], _) -> ValueSome (g.uint16_ty, (start, step, finish))
    | ValApp g g.range_sbyte_op_vref ([], [start; step; finish], _) -> ValueSome (g.sbyte_ty, (start, step, finish))
    | ValApp g g.range_byte_op_vref ([], [start; step; finish], _) -> ValueSome (g.byte_ty, (start, step, finish))
    | ValApp g g.range_char_op_vref ([], [start; finish], _) -> ValueSome (g.char_ty, (start, Expr.Const (Const.Char '\001', Text.Range.range0, g.char_ty), finish))
    | ValApp g g.range_op_vref (ty :: _, [start; finish], _) when isIntegerTy g ty || typeEquivAux EraseMeasures g ty g.char_ty -> ValueSome (ty, (start, mkTypedOne g Text.Range.range0 ty, finish))
    | ValApp g g.range_step_op_vref ([ty; ty2], [start; step; finish], _) when typeEquiv g ty ty2 && (isIntegerTy g ty || typeEquivAux EraseMeasures g ty g.char_ty) -> ValueSome (ty, (start, step, finish))
    | ValApp g g.range_generic_op_vref ([ty; ty2], [_one; _add; start; finish], _) when typeEquiv g ty ty2 && (isIntegerTy g ty || typeEquivAux EraseMeasures g ty g.char_ty) -> ValueSome (ty, (start, mkTypedOne g Text.Range.range0 ty, finish))
    | ValApp g g.range_step_generic_op_vref ([ty; ty2], [_zero; _add; start; step; finish], _) when typeEquiv g ty ty2 && (isIntegerTy g ty || typeEquivAux EraseMeasures g ty g.char_ty) -> ValueSome (ty, (start, step, finish))
    | _ -> ValueNone

/// 5..1
/// 1..-5
/// 1..-1..5
/// -5..-1..-1
/// 5..2..1
[<return: Struct>]
let (|EmptyRange|_|) (start, step, finish) =
    match start, step, finish with
    | Expr.Const (value = Const.Int32 start), Expr.Const (value = Const.Int32 step), Expr.Const (value = Const.Int32 finish) when finish < start && step > 0 || finish > start && step < 0 -> ValueSome EmptyRange
    | Expr.Const (value = Const.Int64 start), Expr.Const (value = Const.Int64 step), Expr.Const (value = Const.Int64 finish) when finish < start && step > 0L || finish > start && step < 0L -> ValueSome EmptyRange
    | Expr.Const (value = Const.UInt64 start), Expr.Const (value = Const.UInt64 _), Expr.Const (value = Const.UInt64 finish) when finish < start -> ValueSome EmptyRange
    | Expr.Const (value = Const.UInt32 start), Expr.Const (value = Const.UInt32 _), Expr.Const (value = Const.UInt32 finish) when finish < start -> ValueSome EmptyRange

    // sizeof<nativeint> is not constant, so |𝑐| ≥ 0x80000000n cannot be treated as a constant.
    | Expr.Const (value = Const.IntPtr start), Expr.Const (value = Const.IntPtr step), Expr.Const (value = Const.IntPtr finish) when
        uint64 start < 0x80000000UL
        && uint64 step < 0x80000000UL
        && uint64 finish < 0x80000000UL
        && (finish < start && step > 0L || finish > start && step < 0L)
        ->
        ValueSome EmptyRange

    // sizeof<unativeint> is not constant, so |𝑐| > 0xffffffffun cannot be treated as a constant.
    | Expr.Const (value = Const.UIntPtr start), Expr.Const (value = Const.UIntPtr step), Expr.Const (value = Const.UIntPtr finish) when
        start <= 0xffffffffUL
        && step <= 0xffffffffUL
        && finish <= 0xffffffffUL
        && finish <= start
        ->
        ValueSome EmptyRange

    | Expr.Const (value = Const.Int16 start), Expr.Const (value = Const.Int16 step), Expr.Const (value = Const.Int16 finish) when finish < start && step > 0s || finish > start && step < 0s -> ValueSome EmptyRange
    | Expr.Const (value = Const.UInt16 start), Expr.Const (value = Const.UInt16 _), Expr.Const (value = Const.UInt16 finish) when finish < start -> ValueSome EmptyRange
    | Expr.Const (value = Const.SByte start), Expr.Const (value = Const.SByte step), Expr.Const (value = Const.SByte finish) when finish < start && step > 0y || finish > start && step < 0y -> ValueSome EmptyRange
    | Expr.Const (value = Const.Byte start), Expr.Const (value = Const.Byte _), Expr.Const (value = Const.Byte finish) when finish < start -> ValueSome EmptyRange
    | Expr.Const (value = Const.Char start), Expr.Const (value = Const.Char _), Expr.Const (value = Const.Char finish) when finish < start -> ValueSome EmptyRange
    | _ -> ValueNone

/// Note: this assumes that an empty range has already been checked for
/// (otherwise the conversion operations here might overflow).
[<return: Struct>]
let (|ConstCount|_|) (start, step, finish) =
    match start, step, finish with
    // The count for these ranges is 2⁶⁴ + 1. We must handle such ranges at runtime.
    | Expr.Const (value = Const.Int64 Int64.MinValue), Expr.Const (value = Const.Int64 1L), Expr.Const (value = Const.Int64 Int64.MaxValue)
    | Expr.Const (value = Const.Int64 Int64.MaxValue), Expr.Const (value = Const.Int64 -1L), Expr.Const (value = Const.Int64 Int64.MinValue)
    | Expr.Const (value = Const.UInt64 UInt64.MinValue), Expr.Const (value = Const.UInt64 1UL), Expr.Const (value = Const.UInt64 UInt64.MaxValue)
    | Expr.Const (value = Const.IntPtr Int64.MinValue), Expr.Const (value = Const.IntPtr 1L), Expr.Const (value = Const.IntPtr Int64.MaxValue)
    | Expr.Const (value = Const.IntPtr Int64.MaxValue), Expr.Const (value = Const.IntPtr -1L), Expr.Const (value = Const.IntPtr Int64.MinValue)
    | Expr.Const (value = Const.UIntPtr UInt64.MinValue), Expr.Const (value = Const.UIntPtr 1UL), Expr.Const (value = Const.UIntPtr UInt64.MaxValue) -> ValueNone

    // We must special-case a step of Int64.MinValue, since we cannot call abs on it.
    | Expr.Const (value = Const.Int64 start), Expr.Const (value = Const.Int64 Int64.MinValue), Expr.Const (value = Const.Int64 finish) when start <= finish -> ValueSome (Const.UInt64 ((uint64 finish - uint64 start) / (uint64 Int64.MaxValue + 1UL) + 1UL))
    | Expr.Const (value = Const.Int64 start), Expr.Const (value = Const.Int64 Int64.MinValue), Expr.Const (value = Const.Int64 finish) -> ValueSome (Const.UInt64 ((uint64 start - uint64 finish) / (uint64 Int64.MaxValue + 1UL) + 1UL))
    | Expr.Const (value = Const.IntPtr start), Expr.Const (value = Const.IntPtr Int64.MinValue), Expr.Const (value = Const.IntPtr finish) when start <= finish -> ValueSome (Const.UIntPtr ((uint64 start - uint64 finish) / (uint64 Int64.MaxValue + 1UL) + 1UL))
    | Expr.Const (value = Const.IntPtr start), Expr.Const (value = Const.IntPtr Int64.MinValue), Expr.Const (value = Const.IntPtr finish) -> ValueSome (Const.UIntPtr ((uint64 start - uint64 finish) / (uint64 Int64.MaxValue + 1UL) + 1UL))

    | Expr.Const (value = Const.Int64 start), Expr.Const (value = Const.Int64 step), Expr.Const (value = Const.Int64 finish) when start <= finish -> ValueSome (Const.UInt64 ((uint64 finish - uint64 start) / uint64 (abs step) + 1UL))
    | Expr.Const (value = Const.Int64 start), Expr.Const (value = Const.Int64 step), Expr.Const (value = Const.Int64 finish) -> ValueSome (Const.UInt64 ((uint64 start - uint64 finish) / uint64 (abs step) + 1UL))

    // sizeof<nativeint> is not constant, so |𝑐| ≥ 0x80000000n cannot be treated as a constant.
    | Expr.Const (value = Const.IntPtr start), Expr.Const (value = Const.IntPtr step), Expr.Const (value = Const.IntPtr finish) when
        uint64 start < 0x80000000UL
        && uint64 step < 0x80000000UL
        && uint64 finish < 0x80000000UL
        && start <= finish
        ->
        ValueSome (Const.UIntPtr ((uint64 finish - uint64 start) / uint64 (abs step) + 1UL))

    | Expr.Const (value = Const.IntPtr start), Expr.Const (value = Const.IntPtr step), Expr.Const (value = Const.IntPtr finish) when
        uint64 start < 0x80000000UL
        && uint64 step < 0x80000000UL
        && uint64 finish < 0x80000000UL
        ->
        ValueSome (Const.UIntPtr ((uint64 start - uint64 finish) / uint64 (abs step) + 1UL))

    | Expr.Const (value = Const.Int32 start), Expr.Const (value = Const.Int32 step), Expr.Const (value = Const.Int32 finish) when start <= finish -> ValueSome (Const.UInt64 ((uint64 finish - uint64 start) / uint64 (abs (int64 step)) + 1UL))
    | Expr.Const (value = Const.Int32 start), Expr.Const (value = Const.Int32 step), Expr.Const (value = Const.Int32 finish) -> ValueSome (Const.UInt64 ((uint64 start - uint64 finish) / uint64 (abs (int64 step)) + 1UL))

    | Expr.Const (value = Const.Int16 start), Expr.Const (value = Const.Int16 step), Expr.Const (value = Const.Int16 finish) when start <= finish -> ValueSome (Const.UInt32 ((uint finish - uint start) / uint (abs (int step)) + 1u))
    | Expr.Const (value = Const.Int16 start), Expr.Const (value = Const.Int16 step), Expr.Const (value = Const.Int16 finish) -> ValueSome (Const.UInt32 ((uint start - uint finish) / uint (abs (int step)) + 1u))

    | Expr.Const (value = Const.SByte start), Expr.Const (value = Const.SByte step), Expr.Const (value = Const.SByte finish) when start <= finish -> ValueSome (Const.UInt16 ((uint16 finish - uint16 start) / uint16 (abs (int16 step)) + 1us))
    | Expr.Const (value = Const.SByte start), Expr.Const (value = Const.SByte step), Expr.Const (value = Const.SByte finish) -> ValueSome (Const.UInt16 ((uint16 start - uint16 finish) / uint16 (abs (int16 step)) + 1us))

    // sizeof<unativeint> is not constant, so |𝑐| > 0xffffffffun cannot be treated as a constant.
    | Expr.Const (value = Const.UIntPtr start), Expr.Const (value = Const.UIntPtr step), Expr.Const (value = Const.UIntPtr finish) when
        start <= 0xffffffffUL
        && step <= 0xffffffffUL
        && finish <= 0xffffffffUL
        ->
        ValueSome (Const.UIntPtr ((finish - start) / step + 1UL))

    | Expr.Const (value = Const.UInt64 start), Expr.Const (value = Const.UInt64 step), Expr.Const (value = Const.UInt64 finish) when start <= finish -> ValueSome (Const.UInt64 ((finish - start) / step + 1UL))
    | Expr.Const (value = Const.UInt64 start), Expr.Const (value = Const.UInt64 step), Expr.Const (value = Const.UInt64 finish) -> ValueSome (Const.UInt64 ((start - finish) / step + 1UL))
    | Expr.Const (value = Const.UInt32 start), Expr.Const (value = Const.UInt32 step), Expr.Const (value = Const.UInt32 finish) when start <= finish -> ValueSome (Const.UInt64 (uint64 (finish - start) / uint64 step + 1UL))
    | Expr.Const (value = Const.UInt32 start), Expr.Const (value = Const.UInt32 step), Expr.Const (value = Const.UInt32 finish) -> ValueSome (Const.UInt64 (uint64 (start - finish) / uint64 step + 1UL))
    | Expr.Const (value = Const.UInt16 start), Expr.Const (value = Const.UInt16 step), Expr.Const (value = Const.UInt16 finish) when start <= finish -> ValueSome (Const.UInt32 (uint (finish - start) / uint step + 1u))
    | Expr.Const (value = Const.UInt16 start), Expr.Const (value = Const.UInt16 step), Expr.Const (value = Const.UInt16 finish) -> ValueSome (Const.UInt32 (uint (start - finish) / uint step + 1u))
    | Expr.Const (value = Const.Byte start), Expr.Const (value = Const.Byte step), Expr.Const (value = Const.Byte finish) when start <= finish -> ValueSome (Const.UInt16 (uint16 (finish - start) / uint16 step + 1us))
    | Expr.Const (value = Const.Byte start), Expr.Const (value = Const.Byte step), Expr.Const (value = Const.Byte finish) -> ValueSome (Const.UInt16 (uint16 (start - finish) / uint16 step + 1us))
    | Expr.Const (value = Const.Char start), Expr.Const (value = Const.Char step), Expr.Const (value = Const.Char finish) when start <= finish -> ValueSome (Const.UInt32 (uint (finish - start) / uint step + 1u))
    | Expr.Const (value = Const.Char start), Expr.Const (value = Const.Char step), Expr.Const (value = Const.Char finish) -> ValueSome (Const.UInt32 (uint (start - finish) / uint step + 1u))

    | _ -> ValueNone

type Count = Expr
type Idx = Expr
type Elem = Expr
type Body = Expr
type Loop = Expr
type WouldOvf = Expr

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type RangeCount =
    /// An expression representing a count known at compile time.
    | Constant of Count

    /// An expression representing a "count" whose step is known to be zero at compile time.
    /// Evaluating this expression at runtime will raise an exception.
    | ConstantZeroStep of Expr

    /// An expression to compute a count at runtime that will definitely fit in 64 bits without overflow.
    | Safe of Count

    /// A function for building a loop given an expression that may produce a count that
    /// would not fit in 64 bits without overflow, and an expression indicating whether
    /// evaluating the first expression directly would in fact overflow.
    | PossiblyOversize of ((Count -> WouldOvf -> Expr) -> Expr)

/// Makes an expression to compute the iteration count for the given integral range.
let mkRangeCount g m rangeTy rangeExpr start step finish =
    /// This will raise an exception at runtime if step is zero.
    let mkCallAndIgnoreRangeExpr start step finish =
        // Use the potentially-evaluated-and-bound start, step, and finish.
        let rangeExpr =
            match rangeExpr with
            // Type-specific range op (RangeInt32, etc.).
            | Expr.App (funcExpr, formalType, tyargs, [_start; _step; _finish], m) -> Expr.App (funcExpr, formalType, tyargs, [start; step; finish], m)
            // Generic range–step op (RangeStepGeneric).
            | Expr.App (funcExpr, formalType, tyargs, [zero; add; _start; _step; _finish], m) -> Expr.App (funcExpr, formalType, tyargs, [zero; add; start; step; finish], m)
            | _ -> error (InternalError ($"Unrecognized range function application '{rangeExpr}'.", m))

        mkSequential
            m
            rangeExpr
            (mkUnit g m)

    let mkSignednessAppropriateClt ty e1 e2 =
        if isSignedIntegerTy g ty then
            mkILAsmClt g m e1 e2
        else
            mkAsmExpr ([AI_clt_un], [], [e1; e2], [g.bool_ty], m)

    let unsignedEquivalent ty =
        if typeEquivAux EraseMeasures g ty g.int64_ty then g.uint64_ty
        elif typeEquivAux EraseMeasures g ty g.int32_ty then g.uint32_ty
        elif typeEquivAux EraseMeasures g ty g.int16_ty then g.uint16_ty
        elif typeEquivAux EraseMeasures g ty g.sbyte_ty then g.byte_ty
        else ty

    /// Find the unsigned type with twice the width of the given type, if available.
    let nextWidestUnsignedTy ty =
        if typeEquivAux EraseMeasures g ty g.int64_ty || typeEquivAux EraseMeasures g ty g.int32_ty || typeEquivAux EraseMeasures g ty g.uint32_ty then
            g.uint64_ty
        elif typeEquivAux EraseMeasures g ty g.int16_ty || typeEquivAux EraseMeasures g ty g.uint16_ty || typeEquivAux EraseMeasures g ty g.char_ty then
            g.uint32_ty
        elif typeEquivAux EraseMeasures g ty g.sbyte_ty || typeEquivAux EraseMeasures g ty g.byte_ty then
            g.uint16_ty
        else
            ty

    /// Convert the value to the next-widest unsigned type.
    /// We do this so that adding one won't result in overflow.
    let mkWiden e =
        if typeEquivAux EraseMeasures g rangeTy g.int32_ty then
            mkAsmExpr ([AI_conv DT_I8], [], [e], [g.uint64_ty], m)
        elif typeEquivAux EraseMeasures g rangeTy g.uint32_ty then
            mkAsmExpr ([AI_conv DT_U8], [], [e], [g.uint64_ty], m)
        elif typeEquivAux EraseMeasures g rangeTy g.int16_ty then
            mkAsmExpr ([AI_conv DT_I4], [], [e], [g.uint32_ty], m)
        elif typeEquivAux EraseMeasures g rangeTy g.uint16_ty || typeEquivAux EraseMeasures g rangeTy g.char_ty then
            mkAsmExpr ([AI_conv DT_U4], [], [e], [g.uint32_ty], m)
        elif typeEquivAux EraseMeasures g rangeTy g.sbyte_ty then
            mkAsmExpr ([AI_conv DT_I2], [], [e], [g.uint16_ty], m)
        elif typeEquivAux EraseMeasures g rangeTy g.byte_ty then
            mkAsmExpr ([AI_conv DT_U2], [], [e], [g.uint16_ty], m)
        else
            e

    /// Expects that |e1| ≥ |e2|.
    let mkDiff e1 e2 = mkAsmExpr ([AI_sub], [], [e1; e2], [unsignedEquivalent (tyOfExpr g e1)], m)

    /// diff / step
    let mkQuotient diff step = mkAsmExpr ([AI_div_un], [], [diff; step], [tyOfExpr g diff], m)

    /// Whether the total count might not fit in 64 bits.
    let couldBeTooBig ty =
        typeEquivAux EraseMeasures g ty g.int64_ty
        || typeEquivAux EraseMeasures g ty g.uint64_ty
        || typeEquivAux EraseMeasures g ty g.nativeint_ty
        || typeEquivAux EraseMeasures g ty g.unativeint_ty

    /// pseudoCount + 1
    let mkAddOne pseudoCount =
        let pseudoCount = mkWiden pseudoCount
        let ty = tyOfExpr g pseudoCount

        if couldBeTooBig rangeTy then
            mkAsmExpr ([AI_add_ovf_un], [], [pseudoCount; mkTypedOne g m ty], [ty], m)
        else
            mkAsmExpr ([AI_add], [], [pseudoCount; mkTypedOne g m ty], [ty], m)

    let mkRuntimeCalc mkThrowIfStepIsZero pseudoCount count =
        if typeEquivAux EraseMeasures g rangeTy g.int64_ty || typeEquivAux EraseMeasures g rangeTy g.uint64_ty then
            RangeCount.PossiblyOversize (fun mkLoopExpr ->
                mkThrowIfStepIsZero
                    (mkCompGenLetIn m (nameof pseudoCount) (tyOfExpr g pseudoCount) pseudoCount (fun (_, pseudoCount) ->
                        let wouldOvf = mkILAsmCeq g m pseudoCount (Expr.Const (Const.UInt64 UInt64.MaxValue, m, g.uint64_ty))
                        mkCompGenLetIn m (nameof wouldOvf) g.bool_ty wouldOvf (fun (_, wouldOvf) ->
                            mkLoopExpr count wouldOvf))))
        elif typeEquivAux EraseMeasures g rangeTy g.nativeint_ty || typeEquivAux EraseMeasures g rangeTy g.unativeint_ty then // We have a nativeint ty whose size we won't know till runtime.
            RangeCount.PossiblyOversize (fun mkLoopExpr ->
                mkThrowIfStepIsZero
                    (mkCompGenLetIn m (nameof pseudoCount) (tyOfExpr g pseudoCount) pseudoCount (fun (_, pseudoCount) ->
                        let wouldOvf =
                            mkCond
                                DebugPointAtBinding.NoneAtInvisible
                                m
                                g.bool_ty
                                (mkILAsmCeq g m (mkAsmExpr ([I_sizeof g.ilg.typ_IntPtr], [], [], [g.uint32_ty], m)) (Expr.Const (Const.UInt32 4u, m, g.uint32_ty)))
                                (mkILAsmCeq g m pseudoCount (Expr.Const (Const.UIntPtr (uint64 UInt32.MaxValue), m, g.unativeint_ty)))
                                (mkILAsmCeq g m pseudoCount (Expr.Const (Const.UIntPtr UInt64.MaxValue, m, g.unativeint_ty)))

                        mkCompGenLetIn m (nameof wouldOvf) g.bool_ty wouldOvf (fun (_, wouldOvf) ->
                            mkLoopExpr count wouldOvf))))
        else
            RangeCount.Safe (mkThrowIfStepIsZero count)

    match start, step, finish with
    // start..0..finish
    | _, Expr.Const (value = IntegralConst.Zero), _ -> RangeCount.ConstantZeroStep (mkSequential m (mkCallAndIgnoreRangeExpr start step finish) (mkTypedZero g m rangeTy))

    // 5..1
    // 1..-1..5
    | EmptyRange -> RangeCount.Constant (mkTypedZero g m rangeTy)

    // 1..5
    // 1..2..5
    // 5..-1..1
    | ConstCount count -> RangeCount.Constant (Expr.Const (count, m, nextWidestUnsignedTy rangeTy))

    // start..finish
    // start..1..finish
    //
    //     if finish < start then 0 else finish - start + 1
    | _, Expr.Const (value = IntegralConst.One), _ ->
        let mkCount mkAddOne =
            let count = mkAddOne (mkDiff finish start)
            let countTy = tyOfExpr g count

            mkCond
                DebugPointAtBinding.NoneAtInvisible
                m
                countTy
                (mkSignednessAppropriateClt rangeTy finish start)
                (mkTypedZero g m countTy)
                count

        match start, finish with
        // The total count could exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 Int64.MinValue), _ | _, Expr.Const (value = Const.Int64 Int64.MaxValue)
        | Expr.Const (value = Const.UInt64 UInt64.MinValue), _ | _, Expr.Const (value = Const.UInt64 UInt64.MaxValue) ->
            mkRuntimeCalc id (mkCount id) (mkCount mkAddOne)

        // The total count could not exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 _), _ | _, Expr.Const (value = Const.Int64 _)
        | Expr.Const (value = Const.UInt64 _), _ | _, Expr.Const (value = Const.UInt64 _) ->
            RangeCount.Safe (mkCount mkAddOne)

        | _ -> mkRuntimeCalc id (mkCount id) (mkCount mkAddOne)

    // (Only possible for signed types.)
    //
    // start..-1..finish
    //
    //     if start < finish then 0 else start - finish + 1
    | _, Expr.Const (value = IntegralConst.MinusOne), _ ->
        let mkCount mkAddOne =
            let count = mkAddOne (mkDiff start finish)
            let countTy = tyOfExpr g count

            mkCond
                DebugPointAtBinding.NoneAtInvisible
                m
                countTy
                (mkSignednessAppropriateClt rangeTy start finish)
                (mkTypedZero g m countTy)
                count

        match start, finish with
        // The total count could exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 Int64.MaxValue), _ | _, Expr.Const (value = Const.Int64 Int64.MinValue) ->
            mkRuntimeCalc id (mkCount id) (mkCount mkAddOne)

        // The total count could not exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 _), _ | _, Expr.Const (value = Const.Int64 _) ->
            RangeCount.Safe (mkCount mkAddOne)

        | _ -> mkRuntimeCalc id (mkCount id) (mkCount mkAddOne)

    // start..2..finish
    //
    //     if finish < start then 0 else (finish - start) / step + 1
    | _, Expr.Const (value = IntegralConst.Positive), _ ->
        let count =
            let count = mkAddOne (mkQuotient (mkDiff finish start) step)
            let countTy = tyOfExpr g count

            mkCond
                DebugPointAtBinding.NoneAtInvisible
                m
                countTy
                (mkSignednessAppropriateClt rangeTy finish start)
                (mkTypedZero g m countTy)
                count

        // We know that the magnitude of step is greater than one,
        // so we know that the total count won't overflow.
        RangeCount.Safe count

    // (Only possible for signed types.)
    //
    // start..-2..finish
    //
    //     if start < finish then 0 else (start - finish) / abs step + 1
    | _, Expr.Const (value = IntegralConst.Negative as negativeStep), _ ->
        let count =
            let count = mkAddOne (mkQuotient (mkDiff start finish) (Expr.Const (IntegralConst.abs negativeStep, m, unsignedEquivalent rangeTy)))
            let countTy = tyOfExpr g count

            mkCond
                DebugPointAtBinding.NoneAtInvisible
                m
                countTy
                (mkSignednessAppropriateClt rangeTy start finish)
                (mkTypedZero g m countTy)
                count

        // We know that the magnitude of step is greater than one,
        // so we know that the total count won't overflow.
        RangeCount.Safe count

    // start..step..finish
    //
    //     if step = 0 then
    //         ignore ((.. ..) start step finish) // Throws.
    //     if 0 < step then
    //         if finish < start then 0 else unsigned (finish - start) / unsigned step + 1
    //     else // step < 0
    //         if start < finish then 0 else unsigned (start - finish) / unsigned (abs step) + 1
    | _, _, _ ->
        // Let the range call throw the appropriate localized
        // exception at runtime if step is zero:
        //
        //     if step = 0 then ignore ((.. ..) start step finish)
        let mkThrowIfStepIsZero count =
            let throwIfStepIsZero =
                mkCond
                    DebugPointAtBinding.NoneAtInvisible
                    m
                    g.unit_ty
                    (mkILAsmCeq g m step (mkTypedZero g m rangeTy))
                    (mkCallAndIgnoreRangeExpr start step finish)
                    (mkUnit g m)

            mkSequential m throwIfStepIsZero count

        let mkCount mkAddOne =
            if isSignedIntegerTy g rangeTy then
                let positiveStep =
                    let count = mkAddOne (mkQuotient (mkDiff finish start) step)
                    let countTy = tyOfExpr g count

                    mkCond
                        DebugPointAtBinding.NoneAtInvisible
                        m
                        countTy
                        (mkSignednessAppropriateClt rangeTy finish start)
                        (mkTypedZero g m countTy)
                        count

                let negativeStep =
                    let absStep = mkAsmExpr ([AI_add], [], [mkAsmExpr ([AI_not], [], [step], [rangeTy], m); mkTypedOne g m rangeTy], [rangeTy], m)
                    let count = mkAddOne (mkQuotient (mkDiff start finish) absStep)
                    let countTy = tyOfExpr g count

                    mkCond
                        DebugPointAtBinding.NoneAtInvisible
                        m
                        countTy
                        (mkSignednessAppropriateClt rangeTy start finish)
                        (mkTypedZero g m countTy)
                        count

                mkCond
                    DebugPointAtBinding.NoneAtInvisible
                    m
                    (tyOfExpr g positiveStep)
                    (mkSignednessAppropriateClt rangeTy (mkTypedZero g m rangeTy) step)
                    positiveStep
                    negativeStep
            else // Unsigned.
                let count = mkAddOne (mkQuotient (mkDiff finish start) step)
                let countTy = tyOfExpr g count

                mkCond
                    DebugPointAtBinding.NoneAtInvisible
                    m
                    countTy
                    (mkSignednessAppropriateClt rangeTy finish start)
                    (mkTypedZero g m countTy)
                    count

        match start, finish with
        // The total count could exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 Int64.MinValue), _ | _, Expr.Const (value = Const.Int64 Int64.MaxValue)
        | Expr.Const (value = Const.Int64 Int64.MaxValue), _ | _, Expr.Const (value = Const.Int64 Int64.MinValue)
        | Expr.Const (value = Const.UInt64 UInt64.MinValue), _ | _, Expr.Const (value = Const.UInt64 UInt64.MaxValue) ->
            mkRuntimeCalc mkThrowIfStepIsZero (mkCount id) (mkCount mkAddOne)

        // The total count could not exceed 2⁶⁴.
        | Expr.Const (value = Const.Int64 _), _ | _, Expr.Const (value = Const.Int64 _)
        | Expr.Const (value = Const.UInt64 _), _ | _, Expr.Const (value = Const.UInt64 _) ->
            RangeCount.Safe (mkThrowIfStepIsZero (mkCount mkAddOne))

        | _ -> mkRuntimeCalc mkThrowIfStepIsZero (mkCount id) (mkCount mkAddOne)

let mkOptimizedRangeLoop (g: TcGlobals) (mBody, mFor, mIn, spInWhile) (rangeTy, rangeExpr) (start, step, finish) (buildLoop: (Count -> ((Idx -> Elem -> Body) -> Loop) -> Expr)) =
    let inline mkLetBindingsIfNeeded f =
        match start, step, finish with
        | (Expr.Const _ | Expr.Val _), (Expr.Const _ | Expr.Val _), (Expr.Const _ | Expr.Val _) ->
            f start step finish
    
        | (Expr.Const _ | Expr.Val _), (Expr.Const _ | Expr.Val _), _ ->
            mkCompGenLetIn finish.Range (nameof finish) rangeTy finish (fun (_, finish) ->
                f start step finish)
    
        | _, (Expr.Const _ | Expr.Val _), (Expr.Const _ | Expr.Val _) ->
            mkCompGenLetIn start.Range (nameof start) rangeTy start (fun (_, start) ->
                f start step finish)
    
        | (Expr.Const _ | Expr.Val _), _, (Expr.Const _ | Expr.Val _) ->
            mkCompGenLetIn step.Range (nameof step) rangeTy step (fun (_, step) ->
                f start step finish)
    
        | _, (Expr.Const _ | Expr.Val _), _ ->
            mkCompGenLetIn start.Range (nameof start) rangeTy start (fun (_, start) ->
                mkCompGenLetIn finish.Range (nameof finish) rangeTy finish (fun (_, finish) ->
                    f start step finish))
    
        | (Expr.Const _ | Expr.Val _), _, _ ->
            mkCompGenLetIn step.Range (nameof step) rangeTy step (fun (_, step) ->
                mkCompGenLetIn finish.Range (nameof finish) rangeTy finish (fun (_, finish) ->
                    f start step finish))
    
        | _, _, (Expr.Const _ | Expr.Val _) ->
            mkCompGenLetIn start.Range (nameof start) rangeTy start (fun (_, start) ->
                mkCompGenLetIn step.Range (nameof step) rangeTy step (fun (_, step) ->
                    f start step finish))
    
        | _, _, _ ->
            mkCompGenLetIn start.Range (nameof start) rangeTy start (fun (_, start) ->
                mkCompGenLetIn step.Range (nameof step) rangeTy step (fun (_, step) ->
                    mkCompGenLetIn finish.Range (nameof finish) rangeTy finish (fun (_, finish) ->
                        f start step finish)))

    mkLetBindingsIfNeeded (fun start step finish ->
        /// Start at 0 and count up through count - 1.
        ///
        ///     while i < count do
        ///         <body>
        ///         i <- i + 1
        let mkCountUpExclusive mkBody count =
            let countTy = tyOfExpr g count

            mkCompGenLetMutableIn mIn "i" countTy (mkTypedZero g mIn countTy) (fun (idxVal, idxVar) ->
                mkCompGenLetMutableIn mIn "loopVar" rangeTy start (fun (loopVal, loopVar) ->
                    // loopVar <- loopVar + step
                    let incrV = mkValSet mIn (mkLocalValRef loopVal) (mkAsmExpr ([AI_add], [], [loopVar; step], [rangeTy], mIn))

                    // i <- i + 1
                    let incrI = mkValSet mIn (mkLocalValRef idxVal) (mkAsmExpr ([AI_add], [], [idxVar; mkTypedOne g mIn countTy], [rangeTy], mIn))

                    // <body>
                    // loopVar <- loopVar + step
                    // i <- i + 1
                    let body = mkSequentials g mBody [mkBody idxVar loopVar; incrV; incrI]

                    // i < count
                    let guard = mkAsmExpr ([AI_clt_un], [], [idxVar; count], [g.bool_ty], mFor)

                    // while i < count do
                    //     <body>
                    //     loopVar <- loopVar + step
                    //     i <- i + 1
                    mkWhile
                        g
                        (
                            spInWhile,
                            WhileLoopForCompiledForEachExprMarker,
                            guard,
                            body,
                            mBody
                        )
                )
            )

        /// Start at 0 and count up till we have wrapped around.
        /// We only emit this if the type is or may be 64-bit and step is not constant,
        /// and we only execute it if step = 1 and |finish - step| = 2⁶⁴ + 1.
        ///
        /// Logically equivalent to (pseudo-code):
        ///
        ///     while true do
        ///         <body>
        ///         loopVar <- loopVar + step
        ///         i <- i + 1
        ///         if i = 0 then break
        let mkCountUpInclusive mkBody countTy =
            mkCompGenLetMutableIn mFor "guard" g.bool_ty (mkTrue g mFor) (fun (guardVal, guardVar) ->
                mkCompGenLetMutableIn mIn "i" countTy (mkTypedZero g mIn countTy) (fun (idxVal, idxVar) ->
                    mkCompGenLetMutableIn mIn "loopVar" rangeTy start (fun (loopVal, loopVar) ->
                        // loopVar <- loopVar + step
                        let incrV = mkValSet mIn (mkLocalValRef loopVal) (mkAsmExpr ([AI_add], [], [loopVar; step], [rangeTy], mIn))

                        // i <- i + 1
                        let incrI = mkValSet mIn (mkLocalValRef idxVal) (mkAsmExpr ([AI_add], [], [idxVar; mkTypedOne g mIn countTy], [rangeTy], mIn))

                        // guard <- i <> 0
                        let breakIfZero = mkValSet mFor (mkLocalValRef guardVal) (mkAsmExpr ([ILInstr.AI_cgt_un], [], [idxVar; mkTypedZero g mFor countTy], [g.bool_ty], mFor))

                        // <body>
                        // loopVar <- loopVar + step
                        // i <- i + 1
                        // guard <- i <> 0
                        let body = mkSequentials g mBody [mkBody idxVar loopVar; incrV; incrI; breakIfZero]

                        // while guard do
                        //     <body>
                        //     loopVar <- loopVar + step
                        //     i <- i + 1
                        //     guard <- i <> 0
                        mkWhile
                            g
                            (
                                spInWhile,
                                WhileLoopForCompiledForEachExprMarker,
                                guardVar,
                                body,
                                mBody
                            )
                    )
                )
            )

        match mkRangeCount g mIn rangeTy rangeExpr start step finish with
        | RangeCount.Constant count ->
            buildLoop count (fun mkBody -> mkCountUpExclusive mkBody count)

        | RangeCount.ConstantZeroStep count ->
            mkCompGenLetIn mIn (nameof count) (tyOfExpr g count) count (fun (_, count) ->
                buildLoop count (fun mkBody -> mkCountUpExclusive mkBody count))

        | RangeCount.Safe count ->
            mkCompGenLetIn mIn (nameof count) (tyOfExpr g count) count (fun (_, count) ->
                buildLoop count (fun mkBody -> mkCountUpExclusive mkBody count))

        | RangeCount.PossiblyOversize calc ->
            calc (fun count wouldOvf ->
                buildLoop count (fun mkBody ->
                    mkCond
                        DebugPointAtBinding.NoneAtInvisible
                        mIn
                        g.unit_ty
                        wouldOvf
                        (mkCountUpInclusive mkBody (tyOfExpr g count))
                        (mkCompGenLetIn mIn (nameof count) (tyOfExpr g count) count (fun (_, count) -> mkCountUpExclusive mkBody count))))
    )

let mkDebugPoint m expr = 
    Expr.DebugPoint(DebugPointAtLeafExpr.Yes m, expr)

type OptimizeForExpressionOptions =
    | OptimizeIntRangesOnly
    | OptimizeAllForExpressions

let DetectAndOptimizeForEachExpression g option expr =
    match option, expr with
    | _, CompiledInt32RangeForEachExpr g (startExpr, (1 | -1 as step), finishExpr, elemVar, bodyExpr, ranges) -> 

           let _mBody, spFor, spIn, _mFor, _mIn, _spInWhile, mWholeExpr = ranges
           let spFor = match spFor with DebugPointAtBinding.Yes mFor -> DebugPointAtFor.Yes mFor | _ -> DebugPointAtFor.No
           mkFastForLoop g (spFor, spIn, mWholeExpr, elemVar, startExpr, (step = 1), finishExpr, bodyExpr)

    | OptimizeAllForExpressions, CompiledForEachExpr g (_enumTy, rangeExpr & IntegralRange g (rangeTy, (start, step, finish)), elemVar, bodyExpr, ranges) when
        g.langVersion.SupportsFeature LanguageFeature.LowerIntegralRangesToFastLoops
        ->
        let mBody, _spFor, _spIn, mFor, mIn, spInWhile, _mWhole = ranges

        mkOptimizedRangeLoop
            g
            (mBody, mFor, mIn, spInWhile)
            (rangeTy, rangeExpr)
            (start, step, finish)
            (fun _count mkLoop -> mkLoop (fun _idxVar loopVar -> mkInvisibleLet elemVar.Range elemVar loopVar bodyExpr))

    | OptimizeAllForExpressions, CompiledForEachExpr g (enumerableTy, enumerableExpr, elemVar, bodyExpr, ranges) ->

         let mBody, spFor, spIn, mFor, mIn, spInWhile, mWholeExpr = ranges

         if isStringTy g enumerableTy then
            // type is string, optimize for expression as:
            //  let $str = enumerable
            //  for $idx = 0 to str.Length - 1 do
            //      let elem = str.[idx]
            //      body elem

            let strVar, strExpr = mkCompGenLocal mFor "str" enumerableTy
            let idxVar, idxExpr = mkCompGenLocal elemVar.Range "idx" g.int32_ty

            let lengthExpr = mkGetStringLength g mFor strExpr
            let charExpr = mkGetStringChar g mFor strExpr idxExpr

            let startExpr = mkZero g mFor
            let finishExpr = mkDecr g mFor lengthExpr
            // for compat reasons, loop item over string is sometimes object, not char
            let loopItemExpr = mkCoerceIfNeeded g elemVar.Type g.char_ty charExpr  
            let bodyExpr = mkInvisibleLet mIn elemVar loopItemExpr bodyExpr
            let forExpr = mkFastForLoop g (DebugPointAtFor.No, spIn, mWholeExpr, idxVar, startExpr, true, finishExpr, bodyExpr)
            let expr = mkLet spFor mFor strVar enumerableExpr forExpr

            expr

         elif isListTy g enumerableTy then
            // type is list, optimize for expression as:
            //  let mutable $currentVar = listExpr
            //  let mutable $nextVar = $tailOrNull
            //  while $guardExpr do
            //    let i = $headExpr
            //    bodyExpr ()
            //    $current <- $next
            //    $next <- $tailOrNull

            let IndexHead = 0
            let IndexTail = 1

            let currentVar, currentExpr = mkMutableCompGenLocal mIn "current" enumerableTy
            let nextVar, nextExpr = mkMutableCompGenLocal mIn "next" enumerableTy
            let elemTy = destListTy g enumerableTy

            let guardExpr = mkNonNullTest g mFor nextExpr
            let headOrDefaultExpr = mkUnionCaseFieldGetUnprovenViaExprAddr (currentExpr, g.cons_ucref, [elemTy], IndexHead, mIn)
            let tailOrNullExpr = mkUnionCaseFieldGetUnprovenViaExprAddr (currentExpr, g.cons_ucref, [elemTy], IndexTail, mIn)

            let bodyExpr =
                mkInvisibleLet mIn elemVar headOrDefaultExpr
                    (mkSequential mIn
                        bodyExpr
                        (mkSequential mIn
                            (mkValSet mIn (mkLocalValRef currentVar) nextExpr)
                            (mkValSet mIn (mkLocalValRef nextVar) tailOrNullExpr)))

            let expr =
                // let mutable current = enumerableExpr
                mkLet spFor mIn currentVar enumerableExpr
                    // let mutable next = current.TailOrNull
                    (mkInvisibleLet mFor nextVar tailOrNullExpr 
                        // while nonNull next do
                       (mkWhile g (spInWhile, WhileLoopForCompiledForEachExprMarker, guardExpr, bodyExpr, mBody)))

            expr

         else
            expr

    | _ -> expr

// Used to remove Expr.Link for inner expressions in pattern matches
let (|InnerExprPat|) expr = stripExpr expr

/// One of the transformations performed by the compiler
/// is to eliminate variables of static type "unit". These is a
/// utility function related to this.

let BindUnitVars g (mvs: Val list, paramInfos: ArgReprInfo list, body) = 
    match mvs, paramInfos with 
    | [v], [] -> 
        assert isUnitTy g v.Type
        [], mkLet DebugPointAtBinding.NoneAtInvisible v.Range v (mkUnit g v.Range) body 
    | _ -> mvs, body

let isThreadOrContextStatic g attrs = 
    HasFSharpAttributeOpt g g.attrib_ThreadStaticAttribute attrs ||
    HasFSharpAttributeOpt g g.attrib_ContextStaticAttribute attrs 

let mkUnitDelayLambda (g: TcGlobals) m e =
    let uv, _ = mkCompGenLocal m "unitVar" g.unit_ty
    mkLambda m uv (e, tyOfExpr g e) 

[<return: Struct>]
let (|UseResumableStateMachinesExpr|_|) g expr =
    match expr with
    | ValApp g g.cgh__useResumableCode_vref (_, _, _m) -> ValueSome ()
    | _ -> ValueNone

/// Match an if...then...else expression or the result of "a && b" or "a || b"
[<return: Struct>]
let (|IfThenElseExpr|_|) expr =
    match expr with
    | Expr.Match (_spBind, _exprm, TDSwitch(cond, [ TCase( DecisionTreeTest.Const (Const.Bool true), TDSuccess ([], 0) )], Some (TDSuccess ([], 1)), _),
                  [| TTarget([], thenExpr, _); TTarget([], elseExpr, _) |], _m, _ty) -> 
        ValueSome (cond, thenExpr,  elseExpr)
    | _ -> ValueNone

/// if __useResumableCode then ... else ...
[<return: Struct>]
let (|IfUseResumableStateMachinesExpr|_|) g expr =
    match expr with
    | IfThenElseExpr(UseResumableStateMachinesExpr g (), thenExpr, elseExpr) -> ValueSome (thenExpr, elseExpr)
    | _ -> ValueNone

/// Combine a list of ModuleOrNamespaceType's making up the description of a CCU. checking there are now
/// duplicate modules etc.
let CombineCcuContentFragments l = 

    /// Combine module types when multiple namespace fragments contribute to the
    /// same namespace, making new module specs as we go.
    let rec CombineModuleOrNamespaceTypes path (mty1: ModuleOrNamespaceType) (mty2: ModuleOrNamespaceType) = 
        let kind = mty1.ModuleOrNamespaceKind
        let tab1 = mty1.AllEntitiesByLogicalMangledName
        let tab2 = mty2.AllEntitiesByLogicalMangledName
        let entities = 
            [
                for e1 in mty1.AllEntities do 
                    match tab2.TryGetValue e1.LogicalName with
                    | true, e2 -> yield CombineEntities path e1 e2
                    | _ -> yield e1

                for e2 in mty2.AllEntities do 
                    match tab1.TryGetValue e2.LogicalName with
                    | true, _ -> ()
                    | _ -> yield e2
            ]

        let vals = QueueList.append mty1.AllValsAndMembers mty2.AllValsAndMembers

        ModuleOrNamespaceType(kind, vals, QueueList.ofList entities)

    and CombineEntities path (entity1: Entity) (entity2: Entity) = 

        let path2 = path@[entity2.DemangledModuleOrNamespaceName]

        match entity1.IsNamespace, entity2.IsNamespace, entity1.IsModule, entity2.IsModule with
        | true, true, _, _ ->
            ()
        | true, _, _, _
        | _, true, _, _ -> 
            errorR(Error(FSComp.SR.tastNamespaceAndModuleWithSameNameInAssembly(textOfPath path2), entity2.Range))
        | false, false, false, false -> 
            errorR(Error(FSComp.SR.tastDuplicateTypeDefinitionInAssembly(entity2.LogicalName, textOfPath path), entity2.Range))
        | false, false, true, true -> 
            errorR(Error(FSComp.SR.tastTwoModulesWithSameNameInAssembly(textOfPath path2), entity2.Range))
        | _ -> 
            errorR(Error(FSComp.SR.tastConflictingModuleAndTypeDefinitionInAssembly(entity2.LogicalName, textOfPath path), entity2.Range))

        entity1 |> Construct.NewModifiedTycon (fun data1 -> 
            let xml = XmlDoc.Merge entity1.XmlDoc entity2.XmlDoc
            { data1 with 
                entity_attribs = entity1.Attribs @ entity2.Attribs
                entity_modul_type = MaybeLazy.Lazy (InterruptibleLazy(fun _ -> CombineModuleOrNamespaceTypes path2 entity1.ModuleOrNamespaceType entity2.ModuleOrNamespaceType))
                entity_opt_data = 
                match data1.entity_opt_data with
                | Some optData -> Some { optData with entity_xmldoc = xml }
                | _ -> Some { Entity.NewEmptyEntityOptData() with entity_xmldoc = xml } }) 
    
    and CombineModuleOrNamespaceTypeList path l = 
        match l with
        | h :: t -> List.fold (CombineModuleOrNamespaceTypes path) h t
        | _ -> failwith "CombineModuleOrNamespaceTypeList"

    CombineModuleOrNamespaceTypeList [] l

/// An immutable mapping from witnesses to some data.
///
/// Note: this uses an immutable HashMap/Dictionary with an IEqualityComparer that captures TcGlobals, see EmptyTraitWitnessInfoHashMap
type TraitWitnessInfoHashMap<'T> = ImmutableDictionary<TraitWitnessInfo, 'T>

/// Create an empty immutable mapping from witnesses to some data
let EmptyTraitWitnessInfoHashMap g : TraitWitnessInfoHashMap<'T> =
    ImmutableDictionary.Create(
        { new IEqualityComparer<_> with 
            member _.Equals(a, b) = nullSafeEquality a b (fun a b -> traitKeysAEquiv g TypeEquivEnv.EmptyIgnoreNulls a b)
            member _.GetHashCode(a) = hash a.MemberName
        })

[<return: Struct>]
let (|WhileExpr|_|) expr = 
    match expr with 
    | Expr.Op (TOp.While (sp1, sp2), _, [Expr.Lambda (_, _, _, [_gv], guardExpr, _, _);Expr.Lambda (_, _, _, [_bv], bodyExpr, _, _)], m) ->
        ValueSome (sp1, sp2, guardExpr, bodyExpr, m)
    | _ -> ValueNone

[<return: Struct>]
let (|TryFinallyExpr|_|) expr = 
    match expr with 
    | Expr.Op (TOp.TryFinally (sp1, sp2), [ty], [Expr.Lambda (_, _, _, [_], e1, _, _); Expr.Lambda (_, _, _, [_], e2, _, _)], m) ->
        ValueSome (sp1, sp2, ty, e1, e2, m)
    | _ -> ValueNone

[<return: Struct>]
let (|IntegerForLoopExpr|_|) expr = 
    match expr with 
    | Expr.Op (TOp.IntegerForLoop (sp1, sp2, style), _, [Expr.Lambda (_, _, _, [_], e1, _, _);Expr.Lambda (_, _, _, [_], e2, _, _);Expr.Lambda (_, _, _, [v], e3, _, _)], m) ->
        ValueSome (sp1, sp2, style, e1, e2, v, e3, m)
    | _ -> ValueNone

[<return: Struct>]
let (|TryWithExpr|_|) expr =
    match expr with 
    | Expr.Op (TOp.TryWith (spTry, spWith), [resTy], [Expr.Lambda (_, _, _, [_], bodyExpr, _, _); Expr.Lambda (_, _, _, [filterVar], filterExpr, _, _); Expr.Lambda (_, _, _, [handlerVar], handlerExpr, _, _)], m) ->
        ValueSome (spTry, spWith, resTy, bodyExpr, filterVar, filterExpr, handlerVar, handlerExpr, m)
    | _ -> ValueNone

[<return: Struct>]
let (|MatchTwoCasesExpr|_|) expr =
    match expr with 
    | Expr.Match (spBind, mExpr, TDSwitch(cond, [ TCase( DecisionTreeTest.UnionCase (ucref, a), TDSuccess ([], tg1) )], Some (TDSuccess ([], tg2)), b), tgs, m, ty) -> 

        // How to rebuild this construct
        let rebuild (cond, ucref, tg1, tg2, tgs) = 
            Expr.Match (spBind, mExpr, TDSwitch(cond, [ TCase( DecisionTreeTest.UnionCase (ucref, a), TDSuccess ([], tg1) )], Some (TDSuccess ([], tg2)), b), tgs, m, ty)

        ValueSome (cond, ucref, tg1, tg2, tgs, rebuild)

    | _ -> ValueNone

/// match e with None -> ... | Some v -> ... or other variations of the same
[<return: Struct>]
let (|MatchOptionExpr|_|) expr =
    match expr with
    | MatchTwoCasesExpr(cond, ucref, tg1, tg2, tgs, rebuildTwoCases) -> 
        let tgNone, tgSome = if ucref.CaseName = "None" then tg1, tg2 else tg2, tg1
        match tgs[tgNone], tgs[tgSome] with 
        | TTarget([], noneBranchExpr, b2), 
          TTarget([], Expr.Let(TBind(unionCaseVar, Expr.Op(TOp.UnionCaseProof a1, a2, a3, a4), a5), 
                               Expr.Let(TBind(someVar, Expr.Op(TOp.UnionCaseFieldGet (a6a, a6b), a7, a8, a9), a10), someBranchExpr, a11, a12), a13, a14), a16) 
              when unionCaseVar.LogicalName = "unionCase" -> 

            // How to rebuild this construct
            let rebuild (cond, noneBranchExpr, someVar, someBranchExpr) =
                let tgs = Array.zeroCreate 2
                tgs[tgNone] <- TTarget([], noneBranchExpr, b2)
                tgs[tgSome] <- TTarget([], Expr.Let(TBind(unionCaseVar, Expr.Op(TOp.UnionCaseProof a1, a2, a3, a4), a5), 
                                                    Expr.Let(TBind(someVar, Expr.Op(TOp.UnionCaseFieldGet (a6a, a6b), a7, a8, a9), a10), someBranchExpr, a11, a12), a13, a14), a16)
                rebuildTwoCases (cond, ucref, tg1, tg2, tgs)

            ValueSome (cond, noneBranchExpr, someVar, someBranchExpr, rebuild)
        | _ -> ValueNone
    | _ -> ValueNone

[<return: Struct>]
let (|ResumableEntryAppExpr|_|) g expr =
    match expr with
    | ValApp g g.cgh__resumableEntry_vref (_, _, _m) -> ValueSome ()
    | _ -> ValueNone

/// Match an (unoptimized) __resumableEntry expression
[<return: Struct>]
let (|ResumableEntryMatchExpr|_|) g expr =
    match expr with
    | Expr.Let(TBind(matchVar, matchExpr, sp1), MatchOptionExpr (Expr.Val(matchVar2, b, c), noneBranchExpr, someVar, someBranchExpr, rebuildMatch), d, e) ->
        match matchExpr with 
        | ResumableEntryAppExpr g () -> 
            if valRefEq g (mkLocalValRef matchVar) matchVar2 then 

                // How to rebuild this construct
                let rebuild (noneBranchExpr, someBranchExpr) =
                    Expr.Let(TBind(matchVar, matchExpr, sp1), rebuildMatch (Expr.Val(matchVar2, b, c), noneBranchExpr, someVar, someBranchExpr), d, e)

                ValueSome (noneBranchExpr, someVar, someBranchExpr, rebuild)

            else ValueNone

        | _ -> ValueNone
    | _ -> ValueNone

[<return: Struct>]
let (|StructStateMachineExpr|_|) g expr =
    match expr with
    | ValApp g g.cgh__stateMachine_vref ([dataTy; _resultTy], [moveNext; setStateMachine; afterCode], _m) ->
        match moveNext, setStateMachine, afterCode with 
        | NewDelegateExpr g (_, [moveNextThisVar], moveNextBody, _, _),
          NewDelegateExpr g (_, [setStateMachineThisVar;setStateMachineStateVar], setStateMachineBody, _, _),
          NewDelegateExpr g (_, [afterCodeThisVar], afterCodeBody, _, _) ->
              ValueSome (dataTy, 
                    (moveNextThisVar, moveNextBody), 
                    (setStateMachineThisVar, setStateMachineStateVar, setStateMachineBody), 
                    (afterCodeThisVar, afterCodeBody))
        | _ -> ValueNone
    | _ -> ValueNone

[<return: Struct>]
let (|ResumeAtExpr|_|) g expr =
    match expr with
    | ValApp g g.cgh__resumeAt_vref (_, [pcExpr], _m) -> ValueSome pcExpr
    | _ -> ValueNone

// Detect __debugPoint calls
[<return: Struct>]
let (|DebugPointExpr|_|) g expr =
    match expr with
    | ValApp g g.cgh__debugPoint_vref (_, [StringExpr debugPointName], _m) -> ValueSome debugPointName
    | _ -> ValueNone

// Detect sequencing constructs in state machine code
[<return: Struct>]
let (|SequentialResumableCode|_|) (g: TcGlobals) expr = 
    match expr with

    // e1; e2
    | Expr.Sequential(e1, e2, NormalSeq, m) ->
        ValueSome (e1, e2, m, (fun e1 e2 -> Expr.Sequential(e1, e2, NormalSeq, m)))

    // let __stack_step = e1 in e2
    | Expr.Let(bind, e2, m, _) when bind.Var.CompiledName(g.CompilerGlobalState).StartsWithOrdinal(stackVarPrefix) ->
        ValueSome (bind.Expr, e2, m, (fun e1 e2 -> mkLet bind.DebugPoint m bind.Var e1 e2))

    | _ -> ValueNone

let mkLabelled m l e = mkCompGenSequential m (Expr.Op (TOp.Label l, [], [], m)) e

let isResumableCodeTy g ty = ty |> stripTyEqns g |> (function TType_app(tcref, _, _) -> tyconRefEq g tcref g.ResumableCode_tcr | _ -> false)

let rec isReturnsResumableCodeTy g ty = 
    if isFunTy g ty then isReturnsResumableCodeTy g (rangeOfFunTy g ty)
    else isResumableCodeTy g ty

[<return: Struct>]
let (|ResumableCodeInvoke|_|) g expr =
    match expr with
    // defn.Invoke x --> let arg = x in [defn][arg/x]
    | Expr.App ((Expr.Val (invokeRef, _, _) as iref), a, b, (f :: args), m) 
            when invokeRef.LogicalName = "Invoke" && isReturnsResumableCodeTy g (tyOfExpr g f) -> 
        ValueSome (iref, f, args, m, (fun (f2, args2) -> Expr.App ((iref, a, b, (f2 :: args2), m))))
    | _ -> ValueNone

let ComputeUseMethodImpl g (v: Val) =
    v.ImplementedSlotSigs |> List.exists (fun slotsig ->
        let oty = slotsig.DeclaringType
        let otcref = tcrefOfAppTy g oty
        let tcref = v.MemberApparentEntity

        // REVIEW: it would be good to get rid of this special casing of Compare and GetHashCode
        isInterfaceTy g oty &&

        (let isCompare =
            tcref.GeneratedCompareToValues.IsSome &&
             (typeEquiv g oty g.mk_IComparable_ty ||
              tyconRefEq g g.system_GenericIComparable_tcref otcref)

         not isCompare) &&

        (let isGenericEquals =
            tcref.GeneratedHashAndEqualsWithComparerValues.IsSome &&
            tyconRefEq g g.system_GenericIEquatable_tcref otcref

         not isGenericEquals) &&

        (let isStructural =
            (tcref.GeneratedCompareToWithComparerValues.IsSome && typeEquiv g oty g.mk_IStructuralComparable_ty) ||
            (tcref.GeneratedHashAndEqualsWithComparerValues.IsSome && typeEquiv g oty g.mk_IStructuralEquatable_ty)

         not isStructural))

[<return: Struct>]
let (|Seq|_|) g expr =
    match expr with
    // use 'seq { ... }' as an indicator
    | ValApp g g.seq_vref ([elemTy], [e], _m) -> ValueSome (e, elemTy)
    | _ -> ValueNone

/// Detect a 'yield x' within a 'seq { ... }'
[<return: Struct>]
let (|SeqYield|_|) g expr =
    match expr with
    | ValApp g g.seq_singleton_vref (_, [arg], m) -> ValueSome (arg, m)
    | _ -> ValueNone

/// Detect a 'expr; expr' within a 'seq { ... }'
[<return: Struct>]
let (|SeqAppend|_|) g expr =
    match expr with
    | ValApp g g.seq_append_vref (_, [arg1; arg2], m) -> ValueSome (arg1, arg2, m)
    | _ -> ValueNone

let isVarFreeInExpr v e = Zset.contains v (freeInExpr CollectTyparsAndLocals e).FreeLocals

/// Detect a 'while gd do expr' within a 'seq { ... }'
[<return: Struct>]
let (|SeqWhile|_|) g expr =
    match expr with
    | ValApp g g.seq_generated_vref (_, [Expr.Lambda (_, _, _, [dummyv], guardExpr, _, _);innerExpr], m) 
         when not (isVarFreeInExpr dummyv guardExpr) ->
        
        // The debug point for 'while' is attached to the innerExpr, see TcSequenceExpression
        let mWhile = innerExpr.Range
        let spWhile = match mWhile.NotedSourceConstruct with NotedSourceConstruct.While -> DebugPointAtWhile.Yes mWhile | _ -> DebugPointAtWhile.No
        ValueSome (guardExpr, innerExpr, spWhile, m)

    | _ ->
        ValueNone

[<return: Struct>]
let (|SeqTryFinally|_|) g expr =
    match expr with
    | ValApp g g.seq_finally_vref (_, [arg1;Expr.Lambda (_, _, _, [dummyv], compensation, _, _) as arg2], m) 
        when not (isVarFreeInExpr dummyv compensation) ->

        // The debug point for 'try' and 'finally' are attached to the first and second arguments
        // respectively, see TcSequenceExpression
        let mTry = arg1.Range
        let mFinally = arg2.Range
        let spTry = match mTry.NotedSourceConstruct with NotedSourceConstruct.Try -> DebugPointAtTry.Yes mTry | _ -> DebugPointAtTry.No
        let spFinally = match mFinally.NotedSourceConstruct with NotedSourceConstruct.Finally -> DebugPointAtFinally.Yes mFinally | _ -> DebugPointAtFinally.No

        ValueSome (arg1, compensation, spTry, spFinally, m)

    | _ ->
        ValueNone

[<return: Struct>]
let (|SeqUsing|_|) g expr =
    match expr with
    | ValApp g g.seq_using_vref ([_;_;elemTy], [resource;Expr.Lambda (_, _, _, [v], body, mBind, _)], m) ->
        // The debug point mFor at the 'use x = ... ' gets attached to the lambda
        let spBind = match mBind.NotedSourceConstruct with NotedSourceConstruct.Binding -> DebugPointAtBinding.Yes mBind | _ -> DebugPointAtBinding.NoneAtInvisible
        ValueSome (resource, v, body, elemTy, spBind, m)
    | _ ->
        ValueNone

[<return: Struct>]
let (|SeqForEach|_|) g expr =
    match expr with
    // Nested for loops are represented by calls to Seq.collect
    | ValApp g g.seq_collect_vref ([_inpElemTy;_enumty2;genElemTy], [Expr.Lambda (_, _, _, [v], body, mIn, _); inp], mFor) ->
        // The debug point mIn at the 'in' gets attached to the first argument, see TcSequenceExpression
        let spIn = match mIn.NotedSourceConstruct with NotedSourceConstruct.InOrTo -> DebugPointAtInOrTo.Yes mIn | _ -> DebugPointAtInOrTo.No
        ValueSome (inp, v, body, genElemTy, mFor, mIn, spIn)

    // "for x in e -> e2" is converted to a call to Seq.map by the F# type checker. This could be removed, except it is also visible in F# quotations.
    | ValApp g g.seq_map_vref ([_inpElemTy;genElemTy], [Expr.Lambda (_, _, _, [v], body, mIn, _); inp], mFor) ->
        let spIn = match mIn.NotedSourceConstruct with NotedSourceConstruct.InOrTo -> DebugPointAtInOrTo.Yes mIn | _ -> DebugPointAtInOrTo.No
        // The debug point mFor at the 'for' gets attached to the first argument, see TcSequenceExpression
        ValueSome (inp, v, mkCallSeqSingleton g body.Range genElemTy body, genElemTy, mFor, mIn, spIn)

    | _ -> ValueNone

[<return: Struct>]
let (|SeqDelay|_|) g expr =
    match expr with
    | ValApp g g.seq_delay_vref ([elemTy], [Expr.Lambda (_, _, _, [v], e, _, _)], _m) 
        when not (isVarFreeInExpr v e) -> 
        ValueSome (e, elemTy)
    | _ -> ValueNone

[<return: Struct>]
let (|SeqEmpty|_|) g expr =
    match expr with
    | ValApp g g.seq_empty_vref (_, [], m) -> ValueSome m
    | _ -> ValueNone

let isFSharpExceptionTy g ty =
    match tryTcrefOfAppTy g ty with
    | ValueSome tcref -> tcref.IsFSharpException
    | _ -> false

[<return: Struct>]
let (|EmptyModuleOrNamespaces|_|) (moduleOrNamespaceContents: ModuleOrNamespaceContents) =
    match moduleOrNamespaceContents with
    | TMDefs(defs = defs) ->
        let mdDefsLength =
            defs
            |> List.count (function
                | ModuleOrNamespaceContents.TMDefRec _
                | ModuleOrNamespaceContents.TMDefs _ -> true
                | _ -> false)

        let emptyModuleOrNamespaces =
            defs
            |> List.choose (function
                | ModuleOrNamespaceContents.TMDefRec _ as defRec
                | ModuleOrNamespaceContents.TMDefs(defs = [ ModuleOrNamespaceContents.TMDefRec _ as defRec ]) ->
                    match defRec with
                    | TMDefRec(bindings = [ ModuleOrNamespaceBinding.Module(mspec, ModuleOrNamespaceContents.TMDefs(defs = defs)) ]) ->
                        defs
                        |> List.forall (function
                            | ModuleOrNamespaceContents.TMDefOpens _
                            | ModuleOrNamespaceContents.TMDefDo _
                            | ModuleOrNamespaceContents.TMDefRec (isRec = true; tycons = []; bindings = []) -> true
                            | _ -> false)
                        |> fun isEmpty -> if isEmpty then Some mspec else None
                    | _ -> None
                | _ -> None)

        if mdDefsLength = emptyModuleOrNamespaces.Length then
            ValueSome emptyModuleOrNamespaces
        else
            ValueNone
    | _ -> ValueNone

let tryFindExtensionAttribute (g: TcGlobals) (attribs: Attrib list): Attrib option =
    attribs
    |> List.tryFind (IsMatchingFSharpAttribute g g.attrib_ExtensionAttribute)

let tryAddExtensionAttributeIfNotAlreadyPresentForModule
    (g: TcGlobals)
    (tryFindExtensionAttributeIn: (Attrib list -> Attrib option) -> Attrib option)
    (moduleEntity: Entity)
    : Entity
    =
    if Option.isSome (tryFindExtensionAttribute g moduleEntity.Attribs) then
        moduleEntity
    else
        match tryFindExtensionAttributeIn (tryFindExtensionAttribute g) with
        | None -> moduleEntity
        | Some extensionAttrib ->
            { moduleEntity with entity_attribs = extensionAttrib :: moduleEntity.Attribs }

let tryAddExtensionAttributeIfNotAlreadyPresentForType
    (g: TcGlobals)
    (tryFindExtensionAttributeIn: (Attrib list -> Attrib option) -> Attrib option)
    (moduleOrNamespaceTypeAccumulator: ModuleOrNamespaceType ref)
    (typeEntity: Entity)
    : Entity
    =
    if Option.isSome (tryFindExtensionAttribute g typeEntity.Attribs) then
        typeEntity
    else
        match tryFindExtensionAttributeIn (tryFindExtensionAttribute g) with
        | None -> typeEntity
        | Some extensionAttrib ->
            moduleOrNamespaceTypeAccumulator.Value.AllEntitiesByLogicalMangledName.TryFind(typeEntity.LogicalName)
            |> Option.iter (fun e ->
                e.entity_attribs <- extensionAttrib :: e.Attribs
            )
            typeEntity

type TypedTreeNode =
    {
        Kind: string
        Name: string
        Children: TypedTreeNode list
    }

let rec visitEntity (entity: Entity) : TypedTreeNode =
    let kind =
        if entity.IsModule then
            "module"
        elif entity.IsNamespace then
            "namespace"
        else
            "other"

    let children =
        if not entity.IsModuleOrNamespace then
            Seq.empty
        else
            seq {
                yield! Seq.map visitEntity entity.ModuleOrNamespaceType.AllEntities
                yield! Seq.map visitVal entity.ModuleOrNamespaceType.AllValsAndMembers
            }

    {
        Kind = kind
        Name = entity.CompiledName
        Children = Seq.toList children
    }

and visitVal (v: Val) : TypedTreeNode =
    let children =
        seq {
            match v.ValReprInfo with
            | None -> ()
            | Some reprInfo ->
                yield!
                    reprInfo.ArgInfos
                    |> Seq.collect (fun argInfos ->
                        argInfos
                        |> Seq.map (fun argInfo -> {
                            Name = argInfo.Name |> Option.map (fun i -> i.idText) |> Option.defaultValue ""
                            Kind = "ArgInfo"
                            Children = []
                        })
                    )

            yield!
                v.Typars
                |> Seq.map (fun typar -> {
                    Name = typar.Name
                    Kind = "Typar"
                    Children = []
                })
        }

    {
        Name = v.CompiledName None
        Kind = "val"
        Children = Seq.toList children
    }

let rec serializeNode (writer: IndentedTextWriter) (addTrailingComma:bool) (node: TypedTreeNode) =
    writer.WriteLine("{")
    // Add indent after opening {
    writer.Indent <- writer.Indent + 1
    
    writer.WriteLine($"\"name\": \"{node.Name}\",")
    writer.WriteLine($"\"kind\": \"{node.Kind}\",")

    if node.Children.IsEmpty then
        writer.WriteLine("\"children\": []")
    else
        writer.WriteLine("\"children\": [")
        
        // Add indent after opening [
        writer.Indent <- writer.Indent + 1
        
        node.Children
        |> List.iteri (fun idx -> serializeNode writer (idx + 1 < node.Children.Length))

        // Remove indent before closing ]
        writer.Indent <- writer.Indent - 1
        writer.WriteLine("]")
    
    // Remove indent before closing }
    writer.Indent <- writer.Indent - 1
    if addTrailingComma then
        writer.WriteLine("},")
    else
        writer.WriteLine("}")
 
let serializeEntity path (entity: Entity) =
    let root = visitEntity entity
    use sw = new System.IO.StringWriter()
    use writer = new IndentedTextWriter(sw)
    serializeNode writer false root
    writer.Flush()
    let json = sw.ToString()
    use out = FileSystem.OpenFileForWriteShim(path, fileMode = System.IO.FileMode.Create)
    out.WriteAllText(json)

let updateSeqTypeIsPrefix (fsharpCoreMSpec: ModuleOrNamespace) =
    let findModuleOrNamespace (name: string) (entity: Entity) =
        if not entity.IsModuleOrNamespace then
            None
        else
            entity.ModuleOrNamespaceType.ModulesAndNamespacesByDemangledName
            |> Map.tryFind name

    findModuleOrNamespace "Microsoft" fsharpCoreMSpec
    |> Option.bind (findModuleOrNamespace "FSharp")
    |> Option.bind (findModuleOrNamespace "Collections")
    |> Option.iter (fun collectionsEntity ->
        collectionsEntity.ModuleOrNamespaceType.AllEntitiesByLogicalMangledName
        |> Map.tryFind "seq`1"
        |> Option.iter (fun seqEntity ->
            seqEntity.entity_flags <-
                EntityFlags(
                    false,
                    seqEntity.entity_flags.IsModuleOrNamespace,
                    seqEntity.entity_flags.PreEstablishedHasDefaultConstructor,
                    seqEntity.entity_flags.HasSelfReferentialConstructor,
                    seqEntity.entity_flags.IsStructRecordOrUnionType
                )
        )
    )

let isTyparOrderMismatch (tps: Typars) (argInfos: CurriedArgInfos) =
    let rec getTyparName (ty: TType) : string list =
        match ty with
        | TType_var (typar = tp) ->
            if tp.Id.idText <> unassignedTyparName then
                [ tp.Id.idText ]
            else
                match tp.Solution with
                | None -> []
                | Some solutionType -> getTyparName solutionType
        | TType_fun(domainType, rangeType, _) -> [ yield! getTyparName domainType; yield! getTyparName rangeType ]
        | TType_anon(tys = ti)
        | TType_app (typeInstantiation = ti)
        | TType_tuple (elementTypes = ti) -> List.collect getTyparName ti 
        | _ -> []

    let typarNamesInArguments =
        argInfos
        |> List.collect (fun argInfos ->
                argInfos
                |> List.collect (fun (ty, _) -> getTyparName ty))
        |> List.distinct

    let typarNamesInDefinition =
        tps |> List.map (fun (tp: Typar) -> tp.Id.idText) |> List.distinct

    typarNamesInArguments.Length = typarNamesInDefinition.Length
    && typarNamesInArguments <> typarNamesInDefinition
