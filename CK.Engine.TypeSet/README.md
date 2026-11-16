# CK.Engine.TypeSet

The types that the engine considers for each BinPath: the BinPaths are grouped by the types they end up with, and
each group computes its named type sets.

[CK.Engine.TypeCollector](https://github.com/signature-opensource/CK-ResourceSpace/blob/stable/CK.Engine.TypeCollector/README.md) gives the assemblies and the types they provide (the
`AssemblyCache` and the `GlobalTypeCache`). This project applies the type configuration of the BinPaths on top of it.

## BinPathTypeGroup

`BinPathTypeGroup.Run( IActivityMonitor, EngineConfiguration )` normalizes the configuration (null on a configuration
error), runs the `AssemblyCache`, then builds the groups. Its `BinPathTypeGroup.Result` exposes `Success`, the
`AssemblyCache`, the `TypeCache`, the `Groups` and a `Signature` of the whole result.

For each BinPath, the types of its assemblies are completed by its configuration, in this order:

1. its `BinPathConfiguration.ExcludedTypes` are removed, with everything that is one of them (see [Exclusion](#exclusion));
2. its `BinPathConfiguration.Types` are added (with their configured kind);
3. the `Types` of its named type sets are added: the inclusion in a set is an inclusion in the BinPath;
4. the `[AlsoRegisterType]` closure of the selection is added: it is the last step, and it cannot bring back an
   excluded type (an error).

The BinPaths that share the same assemblies and the same type configuration form one group: `BinPathTypeGroup.Configurations`
lists them, `BinPathTypeGroup.ConfiguredTypes` holds their types and `BinPathTypeGroup.Signature` identifies them.

When there are several groups, the IRealObject and the IPoco of all of them must be handled by one group: the group that
covers them all comes first, or a "(Unified)" group (`BinPathTypeGroup.IsUnifiedPure`) is created for them.

## Named type sets

`BinPathTypeGroup.TypeSets` are the sets declared by `BinPathConfiguration.TypeSets` (a `TypeSetConfiguration`: a local
party, a client of a party), by their name (case insensitive). A set is computed inside its parent (its `Parent` set,
or the group), parents first:

- **Its packages** (the concrete `IResourceGroup`) are the closure of its roots among the packages of its parent minus
  its `ExcludedTypes` (`ResPackageClosure` of
  [CK.ResourceSpace](https://github.com/signature-opensource/CK-ResourceSpace/blob/stable/CK.ResourceSpace/README.md):
  the required relations, forward only; a reference to a type that is not an `IResourceGroup` is ignored).
- **Its roots** are the packages of its `Types`, plus the roots of the sets below it (they propagate upward: a package
  that a client roots is in its party). A set that has no own roots also has the non optional packages of its
  candidates (`ICachedType.IsOptional`: the `IOptionalResourceGroupAttribute` of the closest class, in its base classes,
  that declares one all say so; an abstract base can make its specializations optional, a specialization can say it is
  not; a configurable type is optional when its activation is `Optional`). This optionality lets an assembly offer more
  than what an application eventually uses while keeping the unused code out.
- **Its other types** are the ones of its parent minus its `ExcludedTypes`.
- The `[AlsoRegisterType]` closure of its members applies last: it cannot bring back a type excluded by the set, one
  of its ancestors or the BinPath (an error).

A root that is excluded from the set (or not in its parent), a required package that is not a candidate are errors.

**A set holds membership only.** A required single abstraction (`[IsSingle]`) keeps its root implementations, or its
only candidate (more than one candidate and no root is an ambiguity: an error). Its uniqueness is not checked here:
the scopes in which an abstraction is unique (a client, a party) are known by the engine of the model that defines
them, which checks them (`ResPackageClosure` receives this decision as a predicate). The sets are part of the group's signature.

### Configurable types

The types drive: a set is given by types. `BinPathTypeGroup.ConfigurableTypes` are the configurable types of the group
(`ICachedType.TryGetConfigurableType`, see the `IConfigurableTypeAttribute` of CK.ResourceSpace.Abstractions): their
configuration name is the key of their section, an alias of the type in the configuration only, that never brings the
type in a set. An invalid declaration is an error of the group (logged once by the type cache). The uniqueness of a name
is checked in its scope (a party, a client, the section of an owner...) by the engine of the model that defines the
scopes. This list is what a configuration tool can offer to choose from.

## Exclusion

An exclusion follows the **Liskov substitution principle**: an instance of a specialization is an instance of its base,
so excluding a type excludes everything that **is** this type: the type itself, its specializations, the
implementations and the extensions of an interface. A generic type definition excludes all its constructed types and
what is one of them (`IRepository<>` excludes `IRepository<User>` and its implementations). `TypeExclusion`
(CK.Engine.Configuration) implements it, for the BinPath (`ExcludedTypes`, `GlobalExcludedTypes`) and the named sets.

So no member is assignable to an excluded type, and the base types of a member need no rule of their own: an
exclusion never "hides" a base from the types that specialize it. Some consequences:

| Excluded | Result |
|----------|--------|
| a base class | every class that derives from it |
| a specialization of a concrete class | the specialization only (its base becomes the final one again) |
| a service interface | all its implementations |
| a primary `IPoco` | the whole family (the secondary interfaces extend it) |
| a secondary `IPoco` | this facet and its extensions, not the primary one |

A requirement on an excluded type is an error, never a silent re-inclusion: an `[AlsoRegisterType]` of a member, a
required package of the closure, a type that a set adds while an ancestor excludes it.
