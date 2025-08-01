using Mono.Linker.Tests.Cases.Expectations.Assertions;

namespace Mono.Linker.Tests.Cases.Inheritance.Interfaces.OnReferenceType.NoKeptCtor
{
    public class ExplicitInterfaceCanBeRemovedFromClassWithOnlyStaticMethodUsed
    {
        public static void Main()
        {
            IUsedInterface p = new UsedClass();
            StaticMethodOnlyUsed.StaticMethod();
            p.Foo();
        }

        [Kept]
        interface IUsedInterface
        {
            [Kept]
            void Foo();
        }

        [Kept]
        [KeptMember(".ctor()")]
        [KeptInterface(typeof(IUsedInterface))]
        class UsedClass : IUsedInterface
        {
            [Kept]
            public void Foo()
            {
            }
        }

        [Kept]
        class StaticMethodOnlyUsed : IUsedInterface
        {
            void IUsedInterface.Foo()
            {
            }

            [Kept]
            public static void StaticMethod()
            {
            }
        }
    }
}
