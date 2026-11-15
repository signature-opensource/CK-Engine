using CK.Core;
using CK.Setup;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;

namespace CK.Engine.TypeCollector;

public sealed partial class BinPathTypeGroup
{
    // The named sets of a group (see TypeSetConfiguration), computed parents first:
    // - the packages (the concrete IResourceGroup) are the closure of the roots among the packages of the parent minus
    //   the excluded ones; the roots are the packages of the Types and the Features of the set, plus the ones of its
    //   descendants (the roots propagate upward); a set without own roots also has the non optional packages;
    // - the other types are the ones of the parent minus the excluded ones;
    // - the [AlsoRegisterType] closure of the members applies last (it ignores any exclusion).
    // Returns null on error.
    static IReadOnlyDictionary<string, IReadOnlySet<ICachedType>>? ComputeTypeSets( IActivityMonitor monitor,
                                                                                   GlobalTypeCache typeCache,
                                                                                   BinPathConfiguration c,
                                                                                   IReadOnlySet<ICachedType> groupTypes,
                                                                                   Dictionary<string, List<ICachedType>> features,
                                                                                   IncrementalHash hasher )
    {
        if( c.TypeSets.Count == 0 ) return ImmutableDictionary<string, IReadOnlySet<ICachedType>>.Empty;
        // The signature is based on the configuration (the group's types are already in it).
        foreach( var s in c.TypeSets.OrderBy( s => s.Name, StringComparer.OrdinalIgnoreCase ) )
        {
            hasher.Append( "TypeSet:" ).Append( s.Name ).Append( "/" ).Append( s.Parent ?? "" );
            foreach( var t in s.ExcludedTypes.Select( t => t.ToCSharpName() ).Order( StringComparer.Ordinal ) ) hasher.Append( "-" ).Append( t );
            foreach( var t in s.Types.Select( t => t.ToCSharpName() ).Order( StringComparer.Ordinal ) ) hasher.Append( "+" ).Append( t );
            foreach( var f in s.ExcludedFeatures.Select( f => f.ToLowerInvariant() ).Order( StringComparer.Ordinal ) ) hasher.Append( "-F:" ).Append( f );
            foreach( var f in s.Features.Select( f => f.ToLowerInvariant() ).Order( StringComparer.Ordinal ) ) hasher.Append( "+F:" ).Append( f );
        }
        bool success = true;
        // The own roots and the excluded types of each set (an excluded type excludes everything that is it: see TypeExclusion).
        var ownRoots = new Dictionary<TypeSetConfiguration, HashSet<ICachedType>>();
        var excluded = new Dictionary<TypeSetConfiguration, List<Type>>();
        foreach( var s in c.TypeSets )
        {
            var roots = new HashSet<ICachedType>( s.Types.Select( typeCache.Get ).Where( IsPackage ) );
            var excludedPackages = new HashSet<ICachedType>();
            success &= ResolveFeatures( monitor, s, s.Features, features, roots );
            success &= ResolveFeatures( monitor, s, s.ExcludedFeatures, features, excludedPackages );
            ownRoots.Add( s, roots );
            excluded.Add( s, [.. s.ExcludedTypes, .. excludedPackages.Select( p => p.Type )] );
        }
        if( !success ) return null;
        // The roots propagate upward.
        var allRoots = ownRoots.ToDictionary( kv => kv.Key, kv => new HashSet<ICachedType>( kv.Value ) );
        foreach( var s in c.TypeSets )
        {
            for( var p = FindParent( c, s ); p != null; p = FindParent( c, p ) )
            {
                allRoots[p].UnionWith( ownRoots[s] );
            }
        }
        var result = new Dictionary<string, IReadOnlySet<ICachedType>>( StringComparer.OrdinalIgnoreCase );
        foreach( var s in c.TypeSets.OrderBy( s => s.Name, StringComparer.OrdinalIgnoreCase ) )
        {
            if( Compute( s ) == null ) success = false;
        }
        return success ? result : null;

        IReadOnlySet<ICachedType>? Compute( TypeSetConfiguration s )
        {
            if( result.TryGetValue( s.Name, out var done ) ) return done;
            var parent = FindParent( c, s );
            IReadOnlySet<ICachedType>? parentMembers = parent == null ? groupTypes : Compute( parent );
            if( parentMembers == null ) return null;
            var ex = excluded[s];
            var candidates = parentMembers.Where( t => IsPackage( t ) && ex.FindExcluder( t.Type ) == null ).ToHashSet();
            var roots = allRoots[s];
            bool rootsOk = true;
            foreach( var r in roots.Where( r => !candidates.Contains( r ) ).OrderBy( r => r.CSharpName ) )
            {
                monitor.Error( $"The package '{r.CSharpName}' is a root of {s} (or of a set below it) but it is excluded from it or it is not in its parent." );
                rootsOk = false;
            }
            if( !rootsOk ) return null;
            if( ownRoots[s].Count == 0 )
            {
                roots = new HashSet<ICachedType>( roots );
                roots.UnionWith( candidates.Where( t => !IsOptional( t ) ) );
            }
            HashSet<ICachedType>? closure;
            using( monitor.OpenTrace( $"Computing the packages of {s} from {roots.Count} roots among {candidates.Count} candidates." ) )
            {
                // A set holds membership only: no single abstraction is unique here. The scopes in which an abstraction
                // is unique (a client, a party) are known by the engine of the model, that checks them.
                closure = ResPackageClosure.Compute( monitor,
                                                     typeCache,
                                                     candidates,
                                                     roots.Contains,
                                                     t => typeof( IResourceGroup ).IsAssignableFrom( t.Type ),
                                                     isUnique: _ => false );
            }
            if( closure == null )
            {
                monitor.Error( $"Unable to compute the packages of {s}." );
                return null;
            }
            var members = new HashSet<ICachedType>( parentMembers.Where( t => !IsPackage( t ) && ex.FindExcluder( t.Type ) == null ) );
            members.UnionWith( closure );
            // The [AlsoRegisterType] cannot bring back a type excluded by this set, its ancestors or the BinPath.
            var alsoTypes = members.GetRegisterAlsoTypesClosure();
            if( alsoTypes != null )
            {
                bool alsoOk = true;
                foreach( var (also, source) in alsoTypes )
                {
                    if( members.Contains( also ) ) continue;
                    Type? excluder = null;
                    TypeSetConfiguration? by = null;
                    for( var e = s; e != null && excluder == null; e = FindParent( c, e ) )
                    {
                        excluder = excluded[e].FindExcluder( also.Type );
                        by = e;
                    }
                    if( excluder == null && c.ExcludedTypes.FindExcluder( also.Type ) is Type b )
                    {
                        excluder = b;
                        by = null;
                    }
                    if( excluder != null )
                    {
                        monitor.Error( $"Type '{also}' is excluded from {s} (by '{excluder:N}' of {(object?)by ?? $"BinPath '{c.Name}'"}) but [AlsoRegisterType<{also.Name}>] on '{source}' requires it." );
                        alsoOk = false;
                    }
                    else
                    {
                        members.Add( also );
                    }
                }
                if( !alsoOk ) return null;
            }
            monitor.Info( $"{s}: {members.Count} of the {parentMembers.Count} types of its parent, {closure.Count} of its {candidates.Count} candidate packages." );
            result.Add( s.Name, members );
            return members;
        }
    }

    static TypeSetConfiguration? FindParent( BinPathConfiguration c, TypeSetConfiguration s ) => s.Parent != null ? c.FindTypeSet( s.Parent ) : null;

    static bool ResolveFeatures( IActivityMonitor monitor,
                                 TypeSetConfiguration s,
                                 IEnumerable<string> names,
                                 Dictionary<string, List<ICachedType>> features,
                                 HashSet<ICachedType> collector )
    {
        bool success = true;
        foreach( var f in names )
        {
            if( features.TryGetValue( f, out var packages ) )
            {
                collector.UnionWith( packages );
            }
            else
            {
                monitor.Error( $"{s}: no package has the feature name '{f}'." );
                success = false;
            }
        }
        return success;
    }

    // A package is a concrete IResourceGroup.
    static bool IsPackage( ICachedType t ) => t.Type.IsClass && !t.Type.IsAbstract && typeof( IResourceGroup ).IsAssignableFrom( t.Type );

    // Optional when all the IOptionalResourceGroupAttribute of the closest class (in the base classes) that declares one
    // say so: an abstract base class can make its specializations optional (a feature driver), a specialization can say
    // it is not.
    static bool IsOptional( ICachedType t )
    {
        for( var b = t; b != null; b = b.BaseType )
        {
            bool any = false;
            foreach( var a in b.RawAttributes.OfType<IOptionalResourceGroupAttribute>() )
            {
                if( !a.IsOptional ) return false;
                any = true;
            }
            if( any ) return true;
        }
        return false;
    }

    // The feature names of the packages (see INamedFeatureAttribute): null on error.
    static Dictionary<string, List<ICachedType>>? CollectFeatureNames( IActivityMonitor monitor, IReadOnlySet<ICachedType> types )
    {
        bool success = true;
        var names = new Dictionary<string, List<ICachedType>>( StringComparer.OrdinalIgnoreCase );
        var multiple = new Dictionary<ICachedType, bool>();
        foreach( var t in types )
        {
            if( !IsPackage( t ) )
            {
                // An abstract class can name its specializations (with a TypeNameSuffix).
                if( !(t.Type.IsClass && t.Type.IsAbstract) && t.RawAttributes.OfType<INamedFeatureAttribute>().Any() )
                {
                    monitor.Error( $"Type '{t.CSharpName}' has a feature name but it is not a package (a concrete IResourceGroup)." );
                    success = false;
                }
                continue;
            }
            // The closest declaration in the base classes.
            INamedFeatureAttribute? attr = null;
            for( var b = t; b != null; b = b.BaseType )
            {
                var declared = b.RawAttributes.OfType<INamedFeatureAttribute>().ToList();
                if( declared.Count > 1 )
                {
                    monitor.Error( $"Type '{b.CSharpName}' has {declared.Count} feature names: a package has at most one." );
                    success = false;
                }
                if( declared.Count > 0 )
                {
                    attr = declared[0];
                    break;
                }
            }
            if( attr == null ) continue;
            var name = GetFeatureName( monitor, t, attr );
            if( name == null )
            {
                success = false;
                continue;
            }
            if( !names.TryGetValue( name, out var packages ) )
            {
                packages = new List<ICachedType>();
                names.Add( name, packages );
            }
            packages.Add( t );
            multiple.Add( t, attr.IsMultiple );
        }
        foreach( var (name, packages) in names )
        {
            if( packages.Count == 1 ) continue;
            var list = packages.Select( p => p.CSharpName ).Order().Concatenate( "', '" );
            if( !packages.All( p => multiple[p] ) )
            {
                monitor.Error( $"The feature name '{name}' is borne by '{list}': a name can be borne by more than one package only when all of them are IsMultiple." );
                success = false;
            }
            else if( packages.Select( IsOptional ).Distinct().Count() > 1 )
            {
                monitor.Error( $"The packages of the feature name '{name}' ('{list}') must agree on their optionality." );
                success = false;
            }
        }
        return success ? names : null;
    }

    static string? GetFeatureName( IActivityMonitor monitor, ICachedType t, INamedFeatureAttribute attr )
    {
        if( (attr.Name == null) == (attr.TypeNameSuffix == null) )
        {
            monitor.Error( $"The feature name of '{t.CSharpName}' must have a Name or a TypeNameSuffix (and not both)." );
            return null;
        }
        string name;
        if( attr.Name != null )
        {
            name = attr.Name;
        }
        else
        {
            var suffix = attr.TypeNameSuffix!;
            var typeName = t.Type.Name;
            if( !typeName.EndsWith( suffix, StringComparison.Ordinal ) || typeName.Length == suffix.Length )
            {
                monitor.Error( $"The feature name of '{t.CSharpName}' is its type name without the suffix '{suffix}': its type name must end with it." );
                return null;
            }
            name = typeName.Substring( 0, typeName.Length - suffix.Length );
        }
        if( !TypeSetConfiguration.IsValidFeatureName( name ) )
        {
            monitor.Error( $"The feature name '{name}' of '{t.CSharpName}' is not valid (letters, digits and dots, starting with a letter)." );
            return null;
        }
        return name;
    }
}
