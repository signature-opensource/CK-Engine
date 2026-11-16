using CK.Core;
using CK.Engine.TypeCollector;
using CK.Setup;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using static CK.Testing.MonitorTestHelper;

namespace CK.Engine.TypeSet.Tests;

[AttributeUsage( AttributeTargets.Class, AllowMultiple = true, Inherited = false )]
public sealed class TestConfigurableAttribute : Attribute, IConfigurableTypeAttribute
{
    public TestConfigurableAttribute( string name, TypeActivation activation )
    {
        Name = name;
        Activation = activation;
    }

    public string Name { get; }

    public TypeActivation Activation { get; }
}

[AttributeUsage( AttributeTargets.Class, Inherited = false )]
public sealed class TestOptionalAttribute : Attribute, IOptionalResourceGroupAttribute
{
    public bool IsOptional => true;
}

[AttributeUsage( AttributeTargets.Class, Inherited = false )]
public sealed class TestOptionalityAttribute : Attribute, IOptionalResourceGroupAttribute
{
    public TestOptionalityAttribute( bool isOptional ) => IsOptional = isOptional;

    public bool IsOptional { get; }
}

[TestOptionality( true )]
public abstract class OptionalBase : IResourceGroup { }

[TestFixture]
public class PackageClosureTests
{
    public sealed class Service { }

    public sealed class Req : IResourceGroup { }

    [TestConfigurable( "One", TypeActivation.ByDefault ), Requires<Req>]
    public sealed class Feature1 : IResourceGroup { }

    [TestConfigurable( "Http", TypeActivation.Required )]
    public sealed class HttpDriver : IResourceGroup { }

    [TestConfigurable( "Opt", TypeActivation.Optional )]
    public sealed class OptFeature : IResourceGroup { }

    [TestOptional]
    public sealed class OptPkg : IResourceGroup { }

    public sealed class Side1 : IResourceGroup { }

    public sealed class Side2 : IResourceGroup { }

    static readonly Type[] _all = [typeof( Service ), typeof( Req ), typeof( Feature1 ), typeof( HttpDriver ), typeof( OptFeature ), typeof( OptPkg ), typeof( Side1 ), typeof( Side2 )];

    static EngineConfiguration Create( params TypeSetConfiguration[] sets ) => Create( _all, sets );

    static EngineConfiguration Create( Type[] types, params TypeSetConfiguration[] sets )
    {
        var c = new EngineConfiguration();
        foreach( var t in types ) c.FirstBinPath.Types.Add( t );
        foreach( var s in sets ) c.FirstBinPath.AddTypeSet( s );
        return c;
    }

    static string[] Run( EngineConfiguration c, string setName )
    {
        var result = BinPathTypeGroup.Run( TestHelper.Monitor, c ).ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        return result.Groups.ShouldHaveSingleItem().TypeSets[setName].Select( t => t.Type.Name ).Order().ToArray();
    }

    static void RunFails( EngineConfiguration c, string expected )
    {
        using( TestHelper.Monitor.CollectTexts( out var texts ) )
        {
            var result = BinPathTypeGroup.Run( TestHelper.Monitor, c );
            (result == null || !result.Success).ShouldBeTrue();
            texts.ShouldContain( t => t.Contains( expected ) );
        }
    }

    [Test]
    public void without_roots_a_set_has_the_non_optional_packages()
    {
        Run( Create( new TypeSetConfiguration( "$Party" ) ), "$Party" )
            .ShouldBe( ["Feature1", "HttpDriver", "Req", "Service", "Side1", "Side2"] );
    }

    [Test]
    public void the_roots_by_type_and_their_closure()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Types.Add( typeof( Feature1 ) );
        // The other types are not packages: they stay.
        Run( Create( party ), "$Party" ).ShouldBe( ["Feature1", "Req", "Service"] );

        party.Types.Add( typeof( HttpDriver ) );
        party.Types.Add( typeof( Side1 ) );
        party.Types.Add( typeof( Side2 ) );
        Run( Create( party ), "$Party" ).ShouldBe( ["Feature1", "HttpDriver", "Req", "Service", "Side1", "Side2"] );
    }

    [Test]
    public void the_configurable_types_are_exposed_by_the_group_even_without_sets()
    {
        var g = BinPathTypeGroup.Run( TestHelper.Monitor, Create() ).ShouldNotBeNull().Groups.ShouldHaveSingleItem();
        g.ConfigurableTypes.Select( t => t.Type.Name ).ShouldBe( ["Feature1", "HttpDriver", "OptFeature"] );
    }

    [Test]
    public void an_optional_configurable_type_is_a_member_only_when_rooted()
    {
        Run( Create( new TypeSetConfiguration( "$Party" ) ), "$Party" ).ShouldNotContain( "OptFeature" );
        var party = new TypeSetConfiguration( "$Party" );
        party.Types.Add( typeof( OptFeature ) );
        Run( Create( party ), "$Party" ).ShouldBe( ["OptFeature", "Service"] );
    }

    public sealed class OptionalByBase : OptionalBase { }

    [TestOptionality( false )]
    public sealed class NotOptional : OptionalBase { }

    [Test]
    public void the_optionality_is_the_closest_declaration_in_the_base_classes()
    {
        Type[] types = [typeof( OptionalByBase ), typeof( NotOptional )];
        Run( Create( types, new TypeSetConfiguration( "$Party" ) ), "$Party" ).ShouldBe( ["NotOptional"] );
    }

    [Test]
    public void the_roots_by_type()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Types.Add( typeof( OptPkg ) );
        Run( Create( party ), "$Party" ).ShouldBe( ["OptPkg", "Service"] );
    }

    [Test]
    public void the_roots_propagate_upward()
    {
        var party = new TypeSetConfiguration( "$Party" );
        var client = new TypeSetConfiguration( "App" ) { Parent = "$Party" };
        client.Types.Add( typeof( OptPkg ) );
        var c = Create( party, client );
        // The party has no own roots: its non optional packages, plus the optional one that its client roots.
        Run( c, "$Party" ).ShouldBe( ["Feature1", "HttpDriver", "OptPkg", "Req", "Service", "Side1", "Side2"] );
        Run( c, "App" ).ShouldBe( ["OptPkg", "Service"] );

        // A party with roots has them and the ones of its clients.
        party.Types.Add( typeof( Feature1 ) );
        Run( Create( party, client ), "$Party" ).ShouldBe( ["Feature1", "OptPkg", "Req", "Service"] );
    }

    [AttributeUsage( AttributeTargets.Interface )]
    public sealed class SingleScopeAttribute : Attribute, ISingleMappingAttribute { }

    [SingleScope]
    public interface IBuild : IResourceGroup { }

    public sealed class NgBuild : IBuild { }

    public sealed class ViteBuild : IBuild { }

    [Requires<IBuild>]
    public sealed class Hosting : IResourceGroup { }

    [Test]
    public void a_set_holds_membership_only_the_uniqueness_is_checked_by_the_engine_of_the_model()
    {
        Type[] types = [typeof( NgBuild ), typeof( ViteBuild ), typeof( Hosting )];
        var party = new TypeSetConfiguration( "$Party" );
        var ng = new TypeSetConfiguration( "Ng" ) { Parent = "$Party" };
        ng.Types.Add( typeof( NgBuild ) );
        ng.Types.Add( typeof( Hosting ) );
        var vite = new TypeSetConfiguration( "Vite" ) { Parent = "$Party" };
        vite.Types.Add( typeof( ViteBuild ) );
        vite.Types.Add( typeof( Hosting ) );
        var c = Create( types, party, ng, vite );
        Run( c, "$Party" ).ShouldBe( ["Hosting", "NgBuild", "ViteBuild"] );
        Run( c, "Ng" ).ShouldBe( ["Hosting", "NgBuild"] );
        Run( c, "Vite" ).ShouldBe( ["Hosting", "ViteBuild"] );

        // A client with both is a membership: its uniqueness is not checked here.
        var both = new TypeSetConfiguration( "Both" ) { Parent = "$Party" };
        both.Types.Add( typeof( NgBuild ) );
        both.Types.Add( typeof( ViteBuild ) );
        both.Types.Add( typeof( Hosting ) );
        Run( Create( types, party, ng, vite, both ), "Both" ).ShouldBe( ["Hosting", "NgBuild", "ViteBuild"] );

        // But a requirement that has more than one candidate and no root is ambiguous: the membership can't be decided.
        var hostOnly = new TypeSetConfiguration( "HostOnly" ) { Parent = "$Party" };
        hostOnly.Types.Add( typeof( Hosting ) );
        RunFails( Create( types, party, ng, vite, hostOnly ), "Ambiguous single abstraction" );
    }

    [Test]
    public void a_set_is_computed_inside_its_parent()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.ExcludedTypes.Add( typeof( Side1 ) );
        party.ExcludedTypes.Add( typeof( Side2 ) );
        party.ExcludedTypes.Add( typeof( Service ) );
        var client = new TypeSetConfiguration( "App" ) { Parent = "$Party" };
        Run( Create( party, client ), "App" ).ShouldBe( ["Feature1", "HttpDriver", "Req"] );
    }

    [Test]
    public void a_required_package_must_be_a_candidate()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Types.Add( typeof( Feature1 ) );
        party.ExcludedTypes.Add( typeof( Req ) );
        RunFails( Create( party ), "references 'CK.Engine.TypeSet.Tests.PackageClosureTests+Req' (Requires) that is not a candidate package" );
    }

    [Test]
    public void a_root_cannot_be_excluded_by_an_ancestor()
    {
        // With the types driving, the configuration detects it (the engine's check of the roots is a safety net).
        var party = new TypeSetConfiguration( "$Party" );
        party.ExcludedTypes.Add( typeof( Feature1 ) );
        var client = new TypeSetConfiguration( "App" ) { Parent = "$Party" };
        client.Types.Add( typeof( Feature1 ) );
        RunFails( Create( party, client ), "that its ancestor TypeSet '$Party' excludes" );
    }

    [TestConfigurable( "NotAPackage", TypeActivation.Optional )] public sealed class NotAPackage { }
    [TestConfigurable( "Bad-Name", TypeActivation.Optional )] public sealed class BadName : IResourceGroup { }
    [TestConfigurable( "NoActivation", TypeActivation.None )] public sealed class NoActivation : IResourceGroup { }
    [TestConfigurable( "One", TypeActivation.Optional ), TestConfigurable( "Two", TypeActivation.Optional )] public sealed class TwoNames : IResourceGroup { }

    [TestCase( typeof( NotAPackage ), "it is not a package (a concrete IResourceGroup)" )]
    [TestCase( typeof( BadName ), "The configuration name 'Bad-Name'" )]
    [TestCase( typeof( NoActivation ), "must be Optional, ByDefault or Required (it is 'None')" )]
    [TestCase( typeof( TwoNames ), "has 2 configurable type attributes" )]
    public void an_invalid_configurable_type_is_an_error_of_the_group( Type t, string expected )
    {
        RunFails( Create( [t], new TypeSetConfiguration( "$Party" ) ), expected );
    }
}
