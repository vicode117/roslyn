﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.ImplementAbstractClass;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.ImplementAbstractClass
{
    [Trait(Traits.Feature, Traits.Features.CodeActionsImplementAbstractClass)]
    public sealed class ImplementAbstractClassTests_ThroughMemberTests : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest
    {
        internal override (DiagnosticAnalyzer, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
            => (null, new CSharpImplementAbstractClassCodeFixProvider());

        private IDictionary<OptionKey, object> AllOptionsOff =>
            OptionsSet(
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedMethods, CSharpCodeStyleOptions.NeverWithSilentEnforcement),
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedConstructors, CSharpCodeStyleOptions.NeverWithSilentEnforcement),
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedOperators, CSharpCodeStyleOptions.NeverWithSilentEnforcement),
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedAccessors, CSharpCodeStyleOptions.NeverWithSilentEnforcement),
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedProperties, CSharpCodeStyleOptions.NeverWithSilentEnforcement),
                 SingleOption(CSharpCodeStyleOptions.PreferExpressionBodiedIndexers, CSharpCodeStyleOptions.NeverWithSilentEnforcement));

        internal Task TestAllOptionsOffAsync(
            string initialMarkup,
            string expectedMarkup,
            IDictionary<OptionKey, object> options = null,
            ParseOptions parseOptions = null)
        {
            options ??= new Dictionary<OptionKey, object>();
            foreach (var kvp in AllOptionsOff)
            {
                options.Add(kvp);
            }

            return TestInRegularAndScriptAsync(
                initialMarkup,
                expectedMarkup,
                index: 1,
                options: options,
                parseOptions: parseOptions);
        }

        // virtual along with abstract?
        // virtual only?

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task FieldInBaseClassIsNotSuggested()
        {
            await TestExactActionSetOfferedAsync(
@"abstract class Base
{
    public Base Inner;

    public abstract void Method();
}

class [|Derived|] : Base
{
}", new[] { "Implement Abstract Class" });
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task FieldInMiddleClassIsNotSuggested()
        {
            await TestExactActionSetOfferedAsync(
@"abstract class Base
{
    public abstract void Method();
}

abstract class Middle : Base
{
    public Base Inner;
}

class [|Derived|] : Base
{
}", new[] { "Implement Abstract Class" });
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task FieldOfSameDerivedTypeIsSuggested()
        {
            await TestAllOptionsOffAsync(
@"abstract class Base
{
    public abstract void Method();
}

class [|Derived|] : Base
{
    Derived inner;
}",
@"abstract class Base
{
    public abstract void Method();
}

class Derived : Base
{
    Derived inner;

    public override void Method()
    {
        inner.Method();
    }
}");
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task FieldOfMoreSpecificTypeIsSuggested()
        {
            await TestAllOptionsOffAsync(
@"abstract class Base
{
    public abstract void Method();
}

class [|Derived|] : Base
{
    DerivedAgain inner;
}

class DerivedAgain : Derived
{
}",
@"abstract class Base
{
    public abstract void Method();
}

class Derived : Base
{
    DerivedAgain inner;

    public override void Method()
    {
        inner.Method();
    }
}

class DerivedAgain : Derived
{
}");
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task FieldOfConstrainedGenericTypeIsSuggested()
        {
            await TestAllOptionsOffAsync(
@"abstract class Base
{
    public abstract void Method();
}

class [|Derived|]<T> : Base where T : Base
{
    T inner;
}",
@"abstract class Base
{
    public abstract void Method();
}

class Derived<T> : Base where T : Base
{
    T inner;

    public override void Method()
    {
        inner.Method();
    }
}");
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task DistinguishableOptionsAreShownForExplicitPropertyWithSameName()
        {
            await TestExactActionSetOfferedAsync(
@"abstract class Base
{
    public abstract void Method();
}

interface IInterface
{
    Inner { get; }
}

class [|Derived|] : Base, IInterface
{
    Base Inner { get; }

    Base IInterface.Inner { get; }
}", new[] { "Implement Abstract Class", "Implement through 'Inner'", "Implement through 'IInterface.Inner'" });
        }

        [Fact, WorkItem(41420, "https://github.com/dotnet/roslyn/issues/41420")]
        public async Task NotOfferedForDynamicFields()
        {
            await TestExactActionSetOfferedAsync(
@"abstract class Base
{
    public abstract void Method();
}

class [|Derived|] : Base
{
    dynamic inner;
}", new[] { "Implement Abstract Class" });
        }
    }
}
