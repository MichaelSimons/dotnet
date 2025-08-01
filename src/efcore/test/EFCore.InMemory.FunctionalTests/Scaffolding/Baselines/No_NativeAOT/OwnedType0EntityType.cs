// <auto-generated />
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Scaffolding;

#pragma warning disable 219, 612, 618
#nullable disable

namespace TestNamespace
{
    [EntityFrameworkInternal]
    public partial class OwnedType0EntityType
    {
        public static RuntimeEntityType Create(RuntimeModel model, RuntimeEntityType baseEntityType = null)
        {
            var runtimeEntityType = model.AddEntityType(
                "Microsoft.EntityFrameworkCore.Scaffolding.CompiledModelTestBase+PrincipalDerived<Microsoft.EntityFrameworkCore.Scaffolding.CompiledModelTestBase+DependentBase<byte?>>.ManyOwned#OwnedType",
                typeof(CompiledModelTestBase.OwnedType),
                baseEntityType,
                sharedClrType: true,
                propertyCount: 13,
                servicePropertyCount: 1,
                foreignKeyCount: 1,
                keyCount: 1);

            var principalDerivedId = runtimeEntityType.AddProperty(
                "PrincipalDerivedId",
                typeof(long),
                afterSaveBehavior: PropertySaveBehavior.Throw,
                sentinel: 0L);

            var principalDerivedAlternateId = runtimeEntityType.AddProperty(
                "PrincipalDerivedAlternateId",
                typeof(Guid),
                afterSaveBehavior: PropertySaveBehavior.Throw,
                sentinel: new Guid("00000000-0000-0000-0000-000000000000"));

            var id = runtimeEntityType.AddProperty(
                "Id",
                typeof(int),
                valueGenerated: ValueGenerated.OnAdd,
                afterSaveBehavior: PropertySaveBehavior.Throw,
                sentinel: 0);

            var details = runtimeEntityType.AddProperty(
                "Details",
                typeof(string),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("Details", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_details", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);

            var number = runtimeEntityType.AddProperty(
                "Number",
                typeof(int),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("Number", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("<Number>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                sentinel: 0);

            var refTypeArray = runtimeEntityType.AddProperty(
                "RefTypeArray",
                typeof(IPAddress[]),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("RefTypeArray", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_refTypeArray", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var refTypeArrayElementType = refTypeArray.SetElementType(typeof(IPAddress));

            var refTypeEnumerable = runtimeEntityType.AddProperty(
                "RefTypeEnumerable",
                typeof(IEnumerable<string>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("RefTypeEnumerable", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_refTypeEnumerable", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var refTypeEnumerableElementType = refTypeEnumerable.SetElementType(typeof(string));

            var refTypeIList = runtimeEntityType.AddProperty(
                "RefTypeIList",
                typeof(IList<string>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("RefTypeIList", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_refTypeIList", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var refTypeIListElementType = refTypeIList.SetElementType(typeof(string));

            var refTypeList = runtimeEntityType.AddProperty(
                "RefTypeList",
                typeof(List<IPAddress>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("RefTypeList", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_refTypeList", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var refTypeListElementType = refTypeList.SetElementType(typeof(IPAddress));

            var valueTypeArray = runtimeEntityType.AddProperty(
                "ValueTypeArray",
                typeof(DateTime[]),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("ValueTypeArray", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_valueTypeArray", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var valueTypeArrayElementType = valueTypeArray.SetElementType(typeof(DateTime));

            var valueTypeEnumerable = runtimeEntityType.AddProperty(
                "ValueTypeEnumerable",
                typeof(IEnumerable<byte>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("ValueTypeEnumerable", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_valueTypeEnumerable", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var valueTypeEnumerableElementType = valueTypeEnumerable.SetElementType(typeof(byte));

            var valueTypeIList = runtimeEntityType.AddProperty(
                "ValueTypeIList",
                typeof(IList<byte>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("ValueTypeIList", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("<ValueTypeIList>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var valueTypeIListElementType = valueTypeIList.SetElementType(typeof(byte));

            var valueTypeList = runtimeEntityType.AddProperty(
                "ValueTypeList",
                typeof(List<short>),
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("ValueTypeList", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                fieldInfo: typeof(CompiledModelTestBase.OwnedType).GetField("_valueTypeList", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                nullable: true);
            var valueTypeListElementType = valueTypeList.SetElementType(typeof(short));

            var context = runtimeEntityType.AddServiceProperty(
                "Context",
                propertyInfo: typeof(CompiledModelTestBase.OwnedType).GetProperty("Context", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                serviceType: typeof(DbContext));

            var key = runtimeEntityType.AddKey(
                new[] { principalDerivedId, principalDerivedAlternateId, id });
            runtimeEntityType.SetPrimaryKey(key);

            return runtimeEntityType;
        }

        public static RuntimeForeignKey CreateForeignKey1(RuntimeEntityType declaringEntityType, RuntimeEntityType principalEntityType)
        {
            var runtimeForeignKey = declaringEntityType.AddForeignKey(new[] { declaringEntityType.FindProperty("PrincipalDerivedId"), declaringEntityType.FindProperty("PrincipalDerivedAlternateId") },
                principalEntityType.FindKey(new[] { principalEntityType.FindProperty("Id"), principalEntityType.FindProperty("AlternateId") }),
                principalEntityType,
                deleteBehavior: DeleteBehavior.Cascade,
                required: true,
                ownership: true);

            var manyOwned = principalEntityType.AddNavigation("ManyOwned",
                runtimeForeignKey,
                onDependent: false,
                typeof(IList<CompiledModelTestBase.OwnedType>),
                fieldInfo: typeof(CompiledModelTestBase.PrincipalDerived<CompiledModelTestBase.DependentBase<byte?>>).GetField("ManyOwned", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                eagerLoaded: true);

            return runtimeForeignKey;
        }

        public static void CreateAnnotations(RuntimeEntityType runtimeEntityType)
        {

            Customize(runtimeEntityType);
        }

        static partial void Customize(RuntimeEntityType runtimeEntityType);
    }
}
