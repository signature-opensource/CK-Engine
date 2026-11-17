using CK.Core;
using CK.TypeCache;
using CK.Setup;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;

namespace CK.TypeSet;

/// <summary>
/// Group of similar <see cref="BinPathConfiguration"/> in terms of types to consider.
/// </summary>
public sealed partial class BinPathTypeGroup
{
    readonly ImmutableArray<BinPathConfiguration> _configurations;
    readonly string _groupName;
    readonly IConfiguredTypeSet _configuredTypes;
    readonly IReadOnlyDictionary<string, IReadOnlySet<ICachedType>> _typeSets;
    readonly IReadOnlyList<ICachedType> _configurableTypes;
    // Regular groups have a BinPathGroup.
    readonly AssemblyCache.BinPathGroup? _assemblyGroup;
    readonly GlobalTypeCache _typeCache;

    // Unified has the AssemblyCache.
    readonly AssemblyCache? _assemblyCache;
    SHA1Value _signature;

    // Regular group.
    BinPathTypeGroup( ImmutableArray<BinPathConfiguration> configurations,
                      string groupName,
                      AssemblyCache.BinPathGroup assemblyGroup,
                      GlobalTypeCache typeCache,
                      IConfiguredTypeSet configuredTypes,
                      IReadOnlyDictionary<string, IReadOnlySet<ICachedType>> typeSets,
                      IReadOnlyList<ICachedType> configurableTypes,
                      SHA1Value signature )
    {
        _typeSets = typeSets;
        _configurableTypes = configurableTypes;
        _configurations = configurations;
        _groupName = groupName;
        _assemblyGroup = assemblyGroup;
        _typeCache = typeCache;
        _configuredTypes = configuredTypes;
        _signature = signature;
    }

    // Unified group (Signature is Zero).
    BinPathTypeGroup( AssemblyCache assemblyCache,
                      GlobalTypeCache typeCache,
                      HashSet<ICachedType> allTypes )
    {
        _configurations = ImmutableArray<BinPathConfiguration>.Empty;
        _groupName = "(Unified)";
        _assemblyCache = assemblyCache;
        _typeCache = typeCache;
        _configuredTypes = new ImmutableConfiguredTypeSet( allTypes );
        _typeSets = ImmutableDictionary<string, IReadOnlySet<ICachedType>>.Empty;
        _configurableTypes = ImmutableArray<ICachedType>.Empty;
    }

    /// <summary>
    /// Gets whether no error occured for this group.
    /// </summary>
    public bool Success => _assemblyGroup?.Success ?? true;

    /// <summary>
    /// Gets the name of this group: the comma separated <see cref="BinPathConfiguration.Name"/> for regular groups
    /// and "(Unified)" when <see cref="IsUnifiedPure"/> is true.
    /// </summary>
    public string GroupName => _groupName;

    /// <summary>
    /// Gets the configurations for this group.
    /// This is empty when <see cref="IsUnifiedPure"/> is true.
    /// <para>
    /// All these configurations result in the same set of assemblies and types.
    /// </para>
    /// </summary>
    public ImmutableArray<BinPathConfiguration> Configurations => _configurations;

    /// <summary>
    /// Gets the global type cache.
    /// </summary>
    public GlobalTypeCache TypeCache => _typeCache;

    /// <summary>
    /// Gets the types to consider in the group.
    /// </summary>
    public IConfiguredTypeSet ConfiguredTypes => _configuredTypes;

    /// <summary>
    /// Gets the named sets of types of the group (see <see cref="BinPathConfiguration.TypeSets"/>), by their name (case
    /// insensitive): subsets of the <see cref="ConfiguredTypes"/>. Empty for the unified group.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<ICachedType>> TypeSets => _typeSets;

    /// <summary>
    /// Gets the configurable types of the group (see <see cref="ICachedType.TryGetConfigurableType"/>), ordered by their
    /// C# name. Empty for the unified group.
    /// <para>
    /// The configuration name of a configurable type is the key of its section: it is an alias of the type in the
    /// configuration only, it never brings the type in a set (the types drive). The uniqueness of a name is checked in
    /// its scope (a party, a client, the section of an owner...) by the engine of the model that defines the scopes.
    /// This is what a configuration tool can offer to choose from.
    /// </para>
    /// </summary>
    public IReadOnlyList<ICachedType> ConfigurableTypes => _configurableTypes;

    /// <summary>
    /// Gets whether this group is the purely unified one.
    /// When true, no similar configuration exist (<see cref="Configurations"/> is empty).
    /// <para>
    /// This unified BinPath has no IAutoService, only IPoco and IRealObject, but all the IPoco and all the IRealObject
    /// of all the other BinPaths.
    /// This BinPath is only used to enables Aspects that interact with the real world to handle every aspect (!) of the IRealObject
    /// during the code generation but this code will never be used.
    /// </para>
    /// </summary>
    public bool IsUnifiedPure => _assemblyCache != null;

    /// <summary>
    /// Gets the assembly level information for this group or null if this
    /// is the unified one.
    /// <para>
    /// Other <see cref="BinPathTypeGroup"/> than this one may share the same AssemblyGroup if their
    /// ExcludedTypes and Types configurations differ.
    /// </para>
    /// </summary>
    public AssemblyCache.BinPathGroup? AssemblyGroup => _assemblyGroup;

    /// <summary>
    /// Gets the assembly cache.
    /// </summary>
    public AssemblyCache AssemblyCache => _assemblyGroup?.AssemblyCache ?? _assemblyCache!;

    /// <summary>
    /// Gets this BinPathTypeGroup digital signature. Based on <see cref="AssemblyCache.BinPathGroup.Signature"/>
    /// and the <see cref="BinPathConfiguration.ExcludedTypes"/> and <see cref="BinPathConfiguration.Types"/>
    /// content.
    /// <para>
    /// This is the <see cref="SHA1Value.Zero"/> if <see cref="IsUnifiedPure"/> is true or <see cref="Success"/> is false.
    /// </para>
    /// </summary>
    public SHA1Value Signature => _signature;

    /// <summary>
    /// Creates one or more <see cref="BinPathTypeGroup"/> from a configuration.
    /// <list type="number">
    ///     <item><see cref="EngineConfiguration.NormalizeConfiguration(IActivityMonitor)"/> is called. On failure, null is returned.</item>
    ///     <item>Assemblies or types are discovered. This may fail: <see cref="Result.Success"/> can be false.</item>
    /// </list>
    /// <para>
    /// </para>
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="configuration">The normalized configuration to handle.</param>
    /// <param name="assemblies">
    /// The result of a previous analysis of the assemblies of this configuration (<see cref="Result.AssemblyResult"/>):
    /// reused when the configuration still has the same BinPaths with the same paths, otherwise the assemblies are
    /// analyzed again.
    /// </param>
    /// <returns>The result (<see cref="Result.Success"/> can be false) or null on configuration error.</returns>
    public static Result? Run( IActivityMonitor monitor, EngineConfiguration configuration, AssemblyCache.Result? assemblies = null )
    {
        Throw.CheckNotNullArgument( monitor );
        Throw.CheckNotNullArgument( configuration );
        using var _ = monitor.OpenInfo( $"Analyzing assemblies and configured types in {configuration.BinPaths.Count} BinPath configurations." );
        if( !configuration.NormalizeConfiguration( monitor ) )
        {
            return null;
        }
        AssemblyCache.Result assemblyResult;
        if( assemblies != null && CanReuse( assemblies, configuration ) )
        {
            monitor.Info( "Reusing the previous analysis of the assemblies." );
            assemblyResult = assemblies;
        }
        else
        {
            if( assemblies != null ) monitor.Info( "The BinPaths changed since the previous analysis of the assemblies: analyzing them again." );
            assemblyResult = AssemblyCache.Run( monitor, configuration );
        }

        // Always produce the groups even if assemblyResult.Success is false.
        bool success = assemblyResult.Success;
        var typeCache = assemblyResult.TypeCache;

        var groups = new List<BinPathTypeGroup>();
        // Ok...
        // 1 - Groups the BinPathConfigurations in all Assembly groups by the key based on ExcludedTypes and Types configurations.
        // 2 - Projects the groups by Assembly group and the BinPathConfigurations that share the same key.
        // 3 - Union them (SelectMany).
        // 4 - Computes the BinPathType.GroupName.
        // 5 - Orders these raw groups by their GroupName to compute the global Result signature.
        var rawGroups = assemblyResult.BinPathGroups.Select( g => g.Configurations.GroupBy( b => new GroupKey( g, b ) ) )
                                                    .SelectMany( gType => gType.Select( tG => (Configurations: tG.OrderBy( b => b.Name ).ToImmutableArray(),
                                                                                              tG.Key.AssemblyGroup) ) )
                                                    .Select( rawGroup => (
                                                                            rawGroup.Configurations,
                                                                            rawGroup.AssemblyGroup,
                                                                            GroupName: rawGroup.Configurations.Length == rawGroup.AssemblyGroup.Configurations.Count
                                                                                        ? rawGroup.AssemblyGroup.GroupName
                                                                                        : rawGroup.Configurations.Select( b => b.Name ).Concatenate( "-" )
                                                                          ) )
                                                    .OrderBy( rawGroup => rawGroup.GroupName );


        // Reusable hasher.
        using var hasher = IncrementalHash.CreateHash( HashAlgorithmName.SHA1 );

        foreach( var (configurations, assemblyGroup, groupName) in rawGroups )
        {
            var sourceName = $"BinTypePathGoup '{groupName}'";
            var c = configurations.First();

            // Clones the assembly configured types as there may be other BinPathTypeGroup that use it
            // (don't currently try to opimize here as it would mutate the assemblyGroup.ConfiguredTypes that should not be mutable
            // since it is publicy exposed... or make it internal).
            var types = new ConfiguredTypeSet( assemblyGroup.ConfiguredTypes );

            // Instead of computing the signature on the final set of types that would require to sort it by type name,
            // we based the start of our signature on the Excluded and Types configurations and rely on the Assembly.BinPathGroup
            // signature for the rest.
            hasher.AppendData( assemblyGroup.Signature.GetBytes().Span );

            // Applying Types and ExcludedTypes configurations to the types provided by the assemblies.
            Throw.DebugAssert( "Configuration normalization did the job.", c.Types.Any( tc => c.ExcludedTypes.FindExcluder( tc.Type ) != null ) is false );

            // An excluded type excludes everything that is it (see TypeExclusion). For the hash, always consider the
            // configured type even if it is not used (must unfortunately order the set).
            foreach( var name in c.ExcludedTypes.Select( t => t.ToCSharpName() ).Order( StringComparer.Ordinal ) )
            {
                hasher.Append( name );
            }
            if( c.ExcludedTypes.Count > 0 )
            {
                foreach( var cT in types.AllTypes.Where( t => c.ExcludedTypes.FindExcluder( t.Type ) != null ).ToList() )
                {
                    types.Remove( cT );
                }
            }
            var includedByConfiguration = c.Types.Select( tc => (Type: assemblyResult.TypeCache.Get( tc.Type ), tc.Kind) )
                                                 .OrderBy( cT => cT.Type.CSharpName );

            foreach( var (type, kind) in includedByConfiguration )
            {
                var msg = AssemblyCache.BinPathGroup.GetConfiguredTypeErrorMessage( typeCache, type, kind );
                if( msg != null )
                {
                    monitor.Error( $"Invalid Type in configuration for: '{type.CSharpName}' {msg}." );
                    success = false;
                }
                else
                {
                    types.Add( monitor, sourceName, type, kind );
                }
                hasher.Append( type.CSharpName ).Append( kind );
            }
            // The inclusion of the named sets propagates upward: their Types are types of the group.
            foreach( var s in c.TypeSets.OrderBy( s => s.Name, StringComparer.OrdinalIgnoreCase ) )
            {
                foreach( var type in s.Types.Select( typeCache.Get ).OrderBy( cT => cT.CSharpName ) )
                {
                    var msg = AssemblyCache.BinPathGroup.GetConfiguredTypeErrorMessage( typeCache, type, ConfigurableAutoServiceKind.None );
                    if( msg != null )
                    {
                        monitor.Error( $"Invalid Type in {s}: '{type.CSharpName}' {msg}." );
                        success = false;
                    }
                    else if( !types.AllTypes.Contains( type ) )
                    {
                        types.Add( monitor, $"{sourceName}, {s}", type, ConfigurableAutoServiceKind.None );
                    }
                }
            }
            // The set of types is ready. We finally extend the set with the [AlsoRegisterType] of the
            // selected types: AlsoRegisterType is the last step that must be applied to any type selection.
            // It cannot bring back an excluded type: this is an error.
            Dictionary<ICachedType, ICachedType>? alsoTypes = types.AllTypes.GetRegisterAlsoTypesClosure();
            if( alsoTypes != null )
            {
                foreach( var (also,source) in alsoTypes )
                {
                    if( types.AllTypes.Contains( also ) ) continue;
                    var excluder = c.ExcludedTypes.FindExcluder( also.Type );
                    if( excluder != null )
                    {
                        monitor.Error( $"Type '{also}' is excluded from BinPath '{c.Name}' (by '{excluder:N}') but [AlsoRegisterType<{also.Name}>] on '{source}' requires it." );
                        success = false;
                    }
                    else
                    {
                        types.Add( also );
                        monitor.Debug( $"Type '{also}' will be considered because of [AlsoRegisterType<{also.Name}>] on '{source}'." );
                    }
                }
            }
            // The configurable types of the group: their invalid declarations are logged once (by the type cache).
            var configurableTypes = ImmutableArray.CreateBuilder<ICachedType>();
            foreach( var t in types.AllTypes.OrderBy( t => t.CSharpName, StringComparer.Ordinal ) )
            {
                if( !t.TryGetConfigurableType( monitor, out var configurable ) ) success = false;
                else if( configurable != null ) configurableTypes.Add( t );
            }
            var typeSets = ComputeTypeSets( monitor, typeCache, c, types.AllTypes, hasher );
            if( typeSets == null )
            {
                success = false;
                typeSets = ImmutableDictionary<string, IReadOnlySet<ICachedType>>.Empty;
            }
            var g = new BinPathTypeGroup( configurations,
                                          groupName,
                                          assemblyGroup,
                                          typeCache,
                                          types,
                                          typeSets,
                                          configurableTypes.ToImmutable(),
                                          new SHA1Value( hasher, resetHasher: true ) );
            groups.Add( g );
        }
        // The groups list is ordered by GroupName. We may compute the signature here... 
        if( assemblyResult.Success )
        {
            HandleUnifiedBinPath( monitor, groups, typeCache );
            // ...but why not waiting the unification and accounting the existence of the UnifiedPure
            // or the reordering of the groups (with the most covering one at the start)?
            foreach( var group in groups ) hasher.AppendData( group.Signature.GetBytes().Span );
        }
        return new Result( assemblyResult, groups, new SHA1Value( hasher, resetHasher: false ), success );
    }

    static void HandleUnifiedBinPath( IActivityMonitor monitor, List<BinPathTypeGroup> result, GlobalTypeCache typeCache )
    {
        Throw.DebugAssert( result.Count != 0 );
        if( result.Count == 1 )
        {
            monitor.Info( "There is only one BinPath to process. Unification is not required." );
        }
        else
        {
            var uTypes = new HashSet<ICachedType>();
            // We have no choice here. We must compute the set of IRealObject and IPoco types only for each group
            // to be able to find the covering one (if it exists) and we cannot know if the covering one is needed
            // until we check that a unified group is required.

            var uSets = result.Select( g => new HashSet<ICachedType>( g.ConfiguredTypes.AllTypes
                                                                        .Where( t => t.Interfaces.Contains( typeCache.KnownTypes.IPoco )
                                                                                     || t.Interfaces.Contains( typeCache.KnownTypes.IRealObject ) ) ) )
                              .ToArray();
            // The covering one is necessarily the biggest one if a unification is not required.
            // And an unification is required when the biggest set is still smaller than the union
            // of all the types.
            int maxCount = 0;
            int maxCountIndex = 0;
            for( int i = 0; i < uSets.Length; i++ )
            {
                var uS = uSets[i];
                uTypes.AddRange( uS );
                if( maxCount < uS.Count )
                {
                    maxCount = uS.Count;
                    maxCountIndex = i;
                }
            }
            Throw.DebugAssert( uTypes.Count >= maxCount );
            if( maxCount == uTypes.Count )
            {
                var covering = result[maxCountIndex];
                monitor.Info( $"No unification required: BinPathGroup '{covering.GroupName}' covers the whole set of {uTypes.Count} IRealObject and IPoco." );
                if( maxCountIndex > 0 )
                {
                    result.RemoveAt( maxCountIndex );
                    result.Insert( 0, covering );
                }
            }
            else
            {
                monitor.Info( $"Unification is required for {uTypes.Count} IRealObject and IPoco." );
                var unified = new BinPathTypeGroup( result[0].AssemblyCache, typeCache, uTypes );
                result.Insert( 0, unified );
            }
        }
    }

    /// <summary>
    /// Overridden to return "BinTypePathGoup 'GroupName'".
    /// </summary>
    /// <returns>A readable string.</returns>
    public override string ToString() => $"BinTypePathGoup '{_groupName}'";

    // The assembly groups reference the BinPathConfiguration instances: the configuration must have the same ones, with the
    // same paths (the assemblies of a BinPath are the ones of its path).
    static bool CanReuse( AssemblyCache.Result assemblies, EngineConfiguration configuration )
    {
        var known = assemblies.BinPathGroups.SelectMany( g => g.Configurations.Select( c => (Group: g, Configuration: c) ) ).ToList();
        return known.Count == configuration.BinPaths.Count
               && configuration.BinPaths.All( b => known.Any( k => ReferenceEquals( k.Configuration, b ) && k.Group.Path == b.Path ) );
    }
}
