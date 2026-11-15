using CK.Core;
using CK.Setup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CK.Engine.TypeCollector;

/// <summary>
/// The rules of the packages that the type sets select: what a package is, whether it is optional, its feature name
/// (see <see cref="INamedFeatureAttribute"/>). They are used by <see cref="BinPathTypeGroup"/> and by any code that must
/// apply them to types (a code generator that describes the packages, a test helper).
/// </summary>
public static class FeaturePackages
{
    /// <summary>
    /// Gets whether a type is a package: a concrete <see cref="IResourceGroup"/>.
    /// </summary>
    /// <param name="t">The type.</param>
    /// <returns>True if it is a package.</returns>
    public static bool IsPackage( ICachedType t ) => t.Type.IsClass && !t.Type.IsAbstract && typeof( IResourceGroup ).IsAssignableFrom( t.Type );

    /// <summary>
    /// Gets whether a package is optional: all the <see cref="IOptionalResourceGroupAttribute"/> of the closest class (in
    /// its base classes) that declares one say so. An abstract base class can make its specializations optional (a feature
    /// driver), a specialization can say it is not.
    /// </summary>
    /// <param name="t">The type.</param>
    /// <returns>True if it is optional.</returns>
    public static bool IsOptional( ICachedType t )
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

    /// <summary>
    /// Gets the feature name of a package (see <see cref="INamedFeatureAttribute"/>): the closest declaration in its base
    /// classes.
    /// </summary>
    /// <param name="monitor">The monitor that logs the errors.</param>
    /// <param name="t">The package.</param>
    /// <param name="name">The feature name, null when the package has none.</param>
    /// <param name="isMultiple">Whether other packages can bear the same name.</param>
    /// <returns>False on error.</returns>
    public static bool TryGetFeatureName( IActivityMonitor monitor, ICachedType t, out string? name, out bool isMultiple )
    {
        name = null;
        isMultiple = false;
        INamedFeatureAttribute? attr = null;
        for( var b = t; b != null; b = b.BaseType )
        {
            var declared = b.RawAttributes.OfType<INamedFeatureAttribute>().ToList();
            if( declared.Count > 1 )
            {
                monitor.Error( $"Type '{b.CSharpName}' has {declared.Count} feature names: a package has at most one." );
                return false;
            }
            if( declared.Count > 0 )
            {
                attr = declared[0];
                break;
            }
        }
        if( attr == null ) return true;
        if( (attr.Name == null) == (attr.TypeNameSuffix == null) )
        {
            monitor.Error( $"The feature name of '{t.CSharpName}' must have a Name or a TypeNameSuffix (and not both)." );
            return false;
        }
        string n;
        if( attr.Name != null )
        {
            n = attr.Name;
        }
        else
        {
            var suffix = attr.TypeNameSuffix!;
            var typeName = t.Type.Name;
            if( !typeName.EndsWith( suffix, StringComparison.Ordinal ) || typeName.Length == suffix.Length )
            {
                monitor.Error( $"The feature name of '{t.CSharpName}' is its type name without the suffix '{suffix}': its type name must end with it." );
                return false;
            }
            n = typeName.Substring( 0, typeName.Length - suffix.Length );
        }
        if( !TypeSetConfiguration.IsValidFeatureName( n ) )
        {
            monitor.Error( $"The feature name '{n}' of '{t.CSharpName}' is not valid (letters, digits and dots, starting with a letter)." );
            return false;
        }
        name = n;
        isMultiple = attr.IsMultiple;
        return true;
    }

    /// <summary>
    /// Collects the feature names of the packages among types, and checks them: a type that has a feature name must be a
    /// package (an abstract class can name its specializations), a name is borne by one package unless all the packages
    /// that bear it are multiple, and the packages of a multiple name agree on their optionality.
    /// </summary>
    /// <param name="monitor">The monitor that logs the errors.</param>
    /// <param name="types">The types to consider.</param>
    /// <returns>The packages of each name (case insensitive) or null on error.</returns>
    public static Dictionary<string, List<ICachedType>>? CollectFeatureNames( IActivityMonitor monitor, IEnumerable<ICachedType> types )
    {
        bool success = true;
        var names = new Dictionary<string, List<ICachedType>>( StringComparer.OrdinalIgnoreCase );
        var multiple = new Dictionary<ICachedType, bool>();
        foreach( var t in types )
        {
            if( !IsPackage( t ) )
            {
                if( !(t.Type.IsClass && t.Type.IsAbstract) && t.RawAttributes.OfType<INamedFeatureAttribute>().Any() )
                {
                    monitor.Error( $"Type '{t.CSharpName}' has a feature name but it is not a package (a concrete IResourceGroup)." );
                    success = false;
                }
                continue;
            }
            if( !TryGetFeatureName( monitor, t, out var name, out var isMultiple ) )
            {
                success = false;
                continue;
            }
            if( name == null ) continue;
            if( !names.TryGetValue( name, out var packages ) )
            {
                packages = new List<ICachedType>();
                names.Add( name, packages );
            }
            packages.Add( t );
            multiple.Add( t, isMultiple );
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
}
