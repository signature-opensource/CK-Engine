using CK.Core;
using CK.Engine.TypeCollector;
using CK.Setup;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using static CK.Testing.MonitorTestHelper;

namespace CK.Engine.TypeSet.Tests;

[AttributeUsage( AttributeTargets.Class, Inherited = false )]
public sealed class TestFeatureAttribute : Attribute, INamedFeatureAttribute
{
    public TestFeatureAttribute( string? name = null ) => Name = name;

    public string? Name { get; }

    public string? TypeNameSuffix { get; init; }

    public bool IsMultiple { get; init; }
}

[AttributeUsage( AttributeTargets.Class, Inherited = false )]
public sealed class TestOptionalAttribute : Attribute, IOptionalResourceGroupAttribute
{
    public bool IsOptional => true;
}

[TestFeature( TypeNameSuffix = "Driver" )]
public abstract class TestDriver : IResourceGroup { }

[TestFixture]
public class PackageClosureTests
{
    public sealed class Service { }

    public sealed class Req : IResourceGroup { }

    [TestFeature( "One" ), Requires<Req>]
    public sealed class Feature1 : IResourceGroup { }

    public sealed class HttpDriver : TestDriver { }

    [TestOptional]
    public sealed class OptPkg : IResourceGroup { }

    [TestFeature( "Side", IsMultiple = true )]
    public sealed class Side1 : IResourceGroup { }

    [TestFeature( "Side", IsMultiple = true )]
    public sealed class Side2 : IResourceGroup { }

    static readonly Type[] _all = [typeof( Service ), typeof( Req ), typeof( Feature1 ), typeof( HttpDriver ), typeof( OptPkg ), typeof( Side1 ), typeof( Side2 )];

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
    public void the_roots_by_feature_name_and_their_closure()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Features.Add( "one" );
        // The other types are not packages: they stay.
        Run( Create( party ), "$Party" ).ShouldBe( ["Feature1", "Req", "Service"] );

        party.Features.Add( "Http" );
        party.Features.Add( "Side" );
        Run( Create( party ), "$Party" ).ShouldBe( ["Feature1", "HttpDriver", "Req", "Service", "Side1", "Side2"] );
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
        party.Features.Add( "One" );
        Run( Create( party, client ), "$Party" ).ShouldBe( ["Feature1", "OptPkg", "Req", "Service"] );
    }

    [Test]
    public void a_set_is_computed_inside_its_parent()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.ExcludedFeatures.Add( "Side" );
        party.ExcludedTypes.Add( typeof( Service ) );
        var client = new TypeSetConfiguration( "App" ) { Parent = "$Party" };
        Run( Create( party, client ), "App" ).ShouldBe( ["Feature1", "HttpDriver", "Req"] );
    }

    [Test]
    public void a_required_package_must_be_a_candidate()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Features.Add( "One" );
        party.ExcludedTypes.Add( typeof( Req ) );
        RunFails( Create( party ), "references 'CK.Engine.TypeSet.Tests.PackageClosureTests+Req' (Requires) that is not a candidate package" );
    }

    [Test]
    public void a_root_must_be_a_candidate()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.ExcludedFeatures.Add( "One" );
        var client = new TypeSetConfiguration( "App" ) { Parent = "$Party" };
        client.Types.Add( typeof( Feature1 ) );
        RunFails( Create( party, client ), "is a root of TypeSet '$Party' (or of a set below it) but it is excluded from it or it is not in its parent." );
    }

    [Test]
    public void an_unknown_feature_name_is_an_error()
    {
        var party = new TypeSetConfiguration( "$Party" );
        party.Features.Add( "Nope" );
        RunFails( Create( party ), "TypeSet '$Party': no package has the feature name 'Nope'." );
    }

    [TestFeature( "Same" )] public sealed class Same1 : IResourceGroup { }
    [TestFeature( "same" )] public sealed class Same2 : IResourceGroup { }
    [TestFeature( "Mixed", IsMultiple = true )] public sealed class Mixed1 : IResourceGroup { }
    [TestFeature( "Mixed", IsMultiple = true ), TestOptional] public sealed class Mixed2 : IResourceGroup { }
    [TestFeature( "NotAPackage" )] public sealed class NotAPackage { }
    [TestFeature( TypeNameSuffix = "Driver" )] public sealed class BadSuffix : IResourceGroup { }
    [TestFeature( "Bad-Name" )] public sealed class BadName : IResourceGroup { }

    [TestCase( typeof( Same1 ), typeof( Same2 ), "a name can be borne by more than one package only when all of them are IsMultiple" )]
    [TestCase( typeof( Mixed1 ), typeof( Mixed2 ), "must agree on their optionality" )]
    [TestCase( typeof( NotAPackage ), null, "has a feature name but it is not a package" )]
    [TestCase( typeof( BadSuffix ), null, "its type name must end with it" )]
    [TestCase( typeof( BadName ), null, "The feature name 'Bad-Name'" )]
    public void feature_name_errors( Type t1, Type? t2, string expected )
    {
        Type[] types = t2 == null ? [t1] : [t1, t2];
        RunFails( Create( types, new TypeSetConfiguration( "$Party" ) ), expected );
    }
}
