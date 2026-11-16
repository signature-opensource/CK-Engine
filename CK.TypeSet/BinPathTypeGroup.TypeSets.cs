using CK.Core;
using CK.TypeCache;
using CK.Setup;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;

namespace CK.TypeSet;

public sealed partial class BinPathTypeGroup
{
    // The named sets of a group (see TypeSetConfiguration), computed parents first:
    // - the packages (the concrete IResourceGroup) are the closure of the roots among the packages of the parent minus
    //   the excluded ones; the roots are the packages of the Types of the set, plus the ones of its
    //   descendants (the roots propagate upward); a set without own roots (that nothing restricts) has all the
    //   packages of its parent, the optional ones too (the playground);
    // - the other types are the ones of the parent minus the excluded ones;
    // - the [AlsoRegisterType] closure of the members applies last (it ignores any exclusion).
    // Returns null on error.
    static IReadOnlyDictionary<string, IReadOnlySet<ICachedType>>? ComputeTypeSets( IActivityMonitor monitor,
                                                                                   GlobalTypeCache typeCache,
                                                                                   BinPathConfiguration c,
                                                                                   IReadOnlySet<ICachedType> groupTypes,
                                                                                   IncrementalHash hasher )
    {
        if( c.TypeSets.Count == 0 ) return ImmutableDictionary<string, IReadOnlySet<ICachedType>>.Empty;
        // The signature is based on the configuration (the group's types are already in it).
        foreach( var s in c.TypeSets.OrderBy( s => s.Name, StringComparer.OrdinalIgnoreCase ) )
        {
            hasher.Append( "TypeSet:" ).Append( s.Name ).Append( "/" ).Append( s.Parent ?? "" );
            foreach( var t in s.ExcludedTypes.Select( t => t.ToCSharpName() ).Order( StringComparer.Ordinal ) ) hasher.Append( "-" ).Append( t );
            foreach( var t in s.Types.Select( t => t.ToCSharpName() ).Order( StringComparer.Ordinal ) ) hasher.Append( "+" ).Append( t );
        }
        // The own roots and the excluded types of each set (an excluded type excludes everything that is it: see TypeExclusion).
        var ownRoots = new Dictionary<TypeSetConfiguration, HashSet<ICachedType>>();
        var excluded = new Dictionary<TypeSetConfiguration, List<Type>>();
        foreach( var s in c.TypeSets )
        {
            ownRoots.Add( s, new HashSet<ICachedType>( s.Types.Select( typeCache.Get ).Where( IsPackage ) ) );
            excluded.Add( s, [.. s.ExcludedTypes] );
        }
        bool success = true;
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
            // A set that nothing restricts (no own roots) keeps the whole playground: all its candidates are roots,
            // the optional ones too. The optionality acts in a restricted set only.
            if( ownRoots[s].Count == 0 )
            {
                roots = candidates;
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

    /// <summary>
    /// Gets whether a type is a package: a concrete <see cref="IResourceGroup"/> (the members of a set that its
    /// closure selects).
    /// </summary>
    static bool IsPackage( ICachedType t ) => t.Type.IsClass && !t.Type.IsAbstract && typeof( IResourceGroup ).IsAssignableFrom( t.Type );
}
