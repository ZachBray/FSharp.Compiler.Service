﻿//----------------------------------------------------------------------------
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

namespace Microsoft.FSharp.Compiler.SourceCodeServices

open System.IO
open System.Collections.Generic
open System.Reflection
open Internal.Utilities
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.Infos
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Build
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Nameres
open Microsoft.FSharp.Compiler.Env
open Microsoft.FSharp.Compiler.Lib
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.Pickle
open Microsoft.FSharp.Compiler.PrettyNaming

[<AutoOpen>]
module Impl = 
    let protect f = 
       ErrorLogger.protectAssemblyExplorationF  
         (fun (asmName,path) -> invalidOp (sprintf "The entity or value '%s' does not exist or is in an unresolved assembly. You may need to add a reference to assembly '%s'" path asmName))
         f

    let makeReadOnlyCollection (arr : seq<'T>) = 
        System.Collections.ObjectModel.ReadOnlyCollection<_>(Seq.toArray arr) :> IList<_>
    let makeXmlDoc (XmlDoc x) = makeReadOnlyCollection (x)
    
    let rescopeEntity viewedCcu (entity : Entity) = 
        match tryRescopeEntity viewedCcu entity with
        | None -> mkLocalEntityRef entity
        | Some eref -> eref

    let entityIsUnresolved(entity:EntityRef) = 
        match entity with
        | ERefNonLocal(NonLocalEntityRef(ccu, _)) -> 
            ccu.IsUnresolvedReference && entity.TryDeref.IsNone
        | _ -> false

    let checkEntityIsResolved(entity:EntityRef) = 
        if entityIsUnresolved(entity) then 
            let poorQualifiedName =
                if entity.nlr.AssemblyName = "mscorlib" then 
                    entity.nlr.DisplayName + ", mscorlib"
                else 
                    entity.nlr.DisplayName + ", " + entity.nlr.Ccu.AssemblyName
            invalidOp (sprintf "The entity '%s' does not exist or is in an unresolved assembly." poorQualifiedName)

    /// Checking accessibility that arise from different compilations needs more care - this is a duplicate of the F# compiler code for this case
    let checkForCrossProjectAccessibility (thisCcu2:CcuThunk, ad2) (thisCcu1, taccess1) = 
        match ad2 with 
        | AccessibleFrom(cpaths2,_) ->
            let nameOfScoRef (thisCcu:CcuThunk) scoref = 
                match scoref with 
                | ILScopeRef.Local -> thisCcu.AssemblyName 
                | ILScopeRef.Assembly aref -> aref.Name 
                | ILScopeRef.Module mref -> mref.Name
            let canAccessCompPathFromCrossProject (CompPath(scoref1,cpath1)) (CompPath(scoref2,cpath2)) =
                let rec loop p1 p2  = 
                    match p1,p2 with 
                    | (a1,k1)::rest1, (a2,k2)::rest2 -> (a1=a2) && (k1=k2) && loop rest1 rest2
                    | [],_ -> true 
                    | _ -> false // cpath1 is longer
                loop cpath1 cpath2 &&
                nameOfScoRef thisCcu1 scoref1 = nameOfScoRef thisCcu2 scoref2
            let canAccessFromCrossProject (TAccess x1) cpath2 = x1 |> List.forall (fun cpath1 -> canAccessCompPathFromCrossProject cpath1 cpath2)
            cpaths2 |> List.exists (canAccessFromCrossProject taccess1) 
        | _ -> true // otherwise use the normal check


    /// Convert an IL member accessibility into an F# accessibility
    let getApproxFSharpAccessibilityOfMember (declaringEntity: EntityRef) (ilAccess : ILMemberAccess) = 
        match ilAccess with 
        | ILMemberAccess.FamilyAndAssembly 
        | ILMemberAccess.Assembly -> 
            taccessPrivate  (CompPath(declaringEntity.CompilationPath.ILScopeRef,[]))

        | ILMemberAccess.CompilerControlled
        | ILMemberAccess.Private ->
            taccessPrivate  declaringEntity.CompilationPath

        // This is an approximation - the thing may actually be nested in a private class, in which case it is not actually "public"
        | ILMemberAccess.Public
        // This is an approximation - the thing is actually "protected", but F# accessibilities can't express "protected", so we report it as "public"
        | ILMemberAccess.FamilyOrAssembly
        | ILMemberAccess.Family ->
            taccessPublic 

    /// Convert an IL type definition accessibility into an F# accessibility
    let getApproxFSharpAccessibilityOfEntity (entity: EntityRef) = 
        match metadataOfTycon entity.Deref with 
        | ProvidedTypeMetadata _info -> 
            // This is an approximation - for generative type providers some type definitions can be private.
            taccessPublic

        | ILTypeMetadata (_,td) -> 
            match td.Access with 
            | ILTypeDefAccess.Public 
            | ILTypeDefAccess.Nested ILMemberAccess.Public -> taccessPublic 
            | ILTypeDefAccess.Private  -> taccessPrivate  (CompPath(entity.CompilationPath.ILScopeRef,[]))
            | ILTypeDefAccess.Nested nested -> getApproxFSharpAccessibilityOfMember entity nested

        | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> 
            entity.Accessibility


            

    type cenv(g:TcGlobals, thisCcu: CcuThunk , tcImports: TcImports) = 
        let amapV = tcImports.GetImportMap()
        let infoReaderV = InfoReader(g, amapV)
        member __.g = g
        member __.amap = amapV
        member __.thisCcu = thisCcu
        member __.infoReader = infoReaderV
        member __.tcImports = tcImports

    let getXmlDocSigForEntity (cenv: cenv) (ent:EntityRef)=
        match ItemDescriptionsImpl.GetXmlDocSigOfEntityRef cenv.infoReader ent.Range ent with
        | Some (_, docsig) -> docsig
        | _ -> ""

type FSharpDisplayContext(denv: TcGlobals -> DisplayEnv) = 
    member x.Contents(g) = denv(g)
    static member Empty = FSharpDisplayContext(fun g -> DisplayEnv.Empty(g))


// delay the realization of 'item' in case it is unresolved
type FSharpSymbol(cenv:cenv, item: (unit -> Item), access: (FSharpSymbol -> CcuThunk -> AccessorDomain -> bool)) =

    member x.Assembly = 
        let ccu = defaultArg (ItemDescriptionsImpl.ccuOfItem cenv.g x.Item) cenv.thisCcu 
        FSharpAssembly(cenv,  ccu)

    member x.IsAccessible(rights: FSharpAccessibilityRights) = access x rights.ThisCcu rights.Contents

    member x.FullName = ItemDescriptionsImpl.FullNameOfItem cenv.g x.Item 

    member x.DeclarationLocation = ItemDescriptionsImpl.rangeOfItem cenv.g None x.Item

    member x.ImplementationLocation = ItemDescriptionsImpl.rangeOfItem cenv.g (Some(false)) x.Item

    member x.SignatureLocation = ItemDescriptionsImpl.rangeOfItem cenv.g (Some(true)) x.Item

    member x.IsEffectivelySameAs(y:FSharpSymbol) = 
        x.Equals(y) || Nameres.ItemsAreEffectivelyEqual cenv.g x.Item y.Item

    member internal x.Item = item()

    member x.DisplayName = item().DisplayName

    // This is actually overridden in all cases below. However some symbols are still just of type FSharpSymbol,
    // see 'FSharpSymbol.Create' further below.
    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpSymbol as otherSymbol -> Nameres.ItemsAreEffectivelyEqual cenv.g x.Item otherSymbol.Item
        |   _ -> false

    override x.GetHashCode() = hash x.ImplementationLocation  

    override x.ToString() = "symbol " + (try item().DisplayName with _ -> "?")


and FSharpEntity(cenv:cenv, entity:EntityRef) = 
    inherit FSharpSymbol(cenv,  
                         (fun () -> 
                              checkEntityIsResolved(entity); 
                              if entity.IsModule then Item.ModuleOrNamespaces [entity] 
                              else Item.UnqualifiedType [entity]), 
                         (fun _this thisCcu2 ad -> 
                             checkForCrossProjectAccessibility (thisCcu2, ad) (cenv.thisCcu, getApproxFSharpAccessibilityOfEntity entity)) 
                             // && AccessibilityLogic.IsEntityAccessible cenv.amap range0 ad entity)
                             )

    // If an entity is in an assembly not available to us in the resolution set,
    // we generally return "false" from predicates like IsClass, since we know
    // nothing about that type.
    let isResolvedAndFSharp() = 
        match entity with
        | ERefNonLocal(NonLocalEntityRef(ccu, _)) -> not ccu.IsUnresolvedReference && ccu.IsFSharp
        | _ -> true

    let isUnresolved() = entityIsUnresolved entity
    let isResolved() = not (isUnresolved())
    let checkIsResolved() = checkEntityIsResolved entity

    member __.Entity = entity
        
    member __.LogicalName = 
        checkIsResolved()
        entity.LogicalName 

    member __.CompiledName = 
        checkIsResolved()
        entity.CompiledName 

    member __.DisplayName = 
        checkIsResolved()
        if entity.IsModuleOrNamespace then entity.DemangledModuleOrNamespaceName
        else entity.DisplayName 

    member __.AccessPath  = 
        checkIsResolved()
        match entity.CompilationPathOpt with 
        | None -> "global" 
        | Some (CompPath(_,[])) -> "global" 
        | Some cp -> buildAccessPath (Some cp)
    
    member __.Namespace  = 
        checkIsResolved()
        match entity.CompilationPathOpt with 
        | None -> None
        | Some (CompPath(_,[])) -> None
        | Some cp when cp.AccessPath |> List.forall (function (_,ModuleOrNamespaceKind.Namespace) -> true | _  -> false) -> 
            Some (buildAccessPath (Some cp))
        | Some _ -> None

    member x.QualifiedName = 
        checkIsResolved()
        let fail() = invalidOp (sprintf "the type '%s' does not have a qualified name" x.LogicalName)
        if entity.IsTypeAbbrev || entity.IsProvidedErasedTycon || entity.IsNamespace then fail()
        match entity.CompiledRepresentation with 
        | CompiledTypeRepr.ILAsmNamed(tref,_,_) -> tref.QualifiedName
        | CompiledTypeRepr.ILAsmOpen _ -> fail()
        
    member x.FullName = 
        checkIsResolved()
        match x.TryFullName with 
        | None -> invalidOp (sprintf "the type '%s' does not have a qualified name" x.LogicalName)
        | Some nm -> nm
    
    member x.TryFullName = 
        if isUnresolved() then None
        elif entity.IsTypeAbbrev || entity.IsProvidedErasedTycon then None
        elif entity.IsNamespace  then Some entity.DemangledModuleOrNamespaceName 
        else
            match entity.CompiledRepresentation with 
            | CompiledTypeRepr.ILAsmNamed(tref,_,_) -> Some tref.FullName
            | CompiledTypeRepr.ILAsmOpen _ -> None   

    member __.DeclarationLocation = 
        checkIsResolved()
        entity.Range

    member x.GenericParameters = 
        checkIsResolved()
        entity.TyparsNoRange |> List.map (fun tp -> FSharpGenericParameter(cenv,  tp)) |> List.toArray |> makeReadOnlyCollection

    member __.IsMeasure = 
        isResolvedAndFSharp() && (entity.TypeOrMeasureKind = TyparKind.Measure)

    member __.IsFSharpModule = 
        isResolvedAndFSharp() && entity.IsModule

    member __.HasFSharpModuleSuffix = 
        isResolvedAndFSharp() && 
        entity.IsModule && 
        (entity.ModuleOrNamespaceType.ModuleOrNamespaceKind = ModuleOrNamespaceKind.FSharpModuleWithSuffix)

    member __.IsValueType  = 
        isResolved() &&
        entity.IsStructOrEnumTycon 

    member x.IsArrayType  = 
        isResolved() &&
        isArrayTyconRef cenv.g entity

    member __.IsProvided  = 
        isResolved() &&
        entity.IsProvided

    member __.IsProvidedAndErased  = 
        isResolved() &&
        entity.IsProvidedErasedTycon

    member __.IsStaticInstantiation  = 
        isResolved() &&
        entity.IsStaticInstantiationTycon

    member __.IsProvidedAndGenerated  = 
        isResolved() &&
        entity.IsProvidedGeneratedTycon

    member __.IsClass = 
        isResolved() &&
        match metadataOfTycon entity.Deref with 
        | ProvidedTypeMetadata info -> info.IsClass
        | ILTypeMetadata (_,td) -> (td.tdKind = ILTypeDefKind.Class)
        | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> entity.Deref.IsFSharpClassTycon

    member __.IsByRef = 
        isResolved() &&
        tyconRefEq cenv.g cenv.g.byref_tcr entity

    member __.IsOpaque = 
        isResolved() &&
        entity.IsHiddenReprTycon

    member __.IsInterface = 
        isResolved() &&
        isInterfaceTyconRef entity

    member __.IsDelegate = 
        isResolved() &&
        match metadataOfTycon entity.Deref with 
        | ProvidedTypeMetadata info -> info.IsDelegate ()
        | ILTypeMetadata (_,td) -> (td.tdKind = ILTypeDefKind.Delegate)
        | FSharpOrArrayOrByrefOrTupleOrExnTypeMetadata -> entity.IsFSharpDelegateTycon

    member __.IsEnum = 
        isResolved() &&
        entity.IsEnumTycon
    
    member __.IsFSharpExceptionDeclaration = 
        isResolvedAndFSharp() && entity.IsExceptionDecl

    member __.IsUnresolved = 
        isUnresolved()

    member __.IsFSharp = 
        isResolvedAndFSharp()

    member __.IsFSharpAbbreviation = 
        isResolvedAndFSharp() && entity.IsTypeAbbrev 

    member __.IsFSharpRecord = 
        isResolvedAndFSharp() && entity.IsRecordTycon

    member __.IsFSharpUnion = 
        isResolvedAndFSharp() && entity.IsUnionTycon

    member __.HasAssemblyCodeRepresentation = 
        isResolvedAndFSharp() && (entity.IsAsmReprTycon || entity.IsMeasureableReprTycon)

    member __.FSharpDelegateSignature =
        checkIsResolved()
        match entity.TypeReprInfo with 
        | TFsObjModelRepr r when entity.IsFSharpDelegateTycon -> 
            match r.fsobjmodel_kind with 
            | TTyconDelegate ss -> FSharpDelegateSignature(cenv,  ss)
            | _ -> invalidOp "not a delegate type"
        | _ -> invalidOp "not a delegate type"
      

    member __.Accessibility = 
        if isUnresolved() then FSharpAccessibility(taccessPublic) else

        FSharpAccessibility(getApproxFSharpAccessibilityOfEntity entity) 

    member __.RepresentationAccessibility = 
        if isUnresolved() then FSharpAccessibility(taccessPublic) else
        FSharpAccessibility(entity.TypeReprAccessibility)

    member x.DeclaredInterfaces = 
        if isUnresolved() then makeReadOnlyCollection [] else
        [ for ty in GetImmediateInterfacesOfType cenv.g cenv.amap range0 (generalizedTyconRef entity) do 
             yield FSharpType(cenv,  ty) ]
        |> makeReadOnlyCollection

    member x.AllInterfaces = 
        if isUnresolved() then makeReadOnlyCollection [] else
        [ for ty in AllInterfacesOfType  cenv.g cenv.amap range0 AllowMultiIntfInstantiations.Yes (generalizedTyconRef entity) do 
             yield FSharpType(cenv,  ty) ]
        |> makeReadOnlyCollection

    member x.BaseType = 
        checkIsResolved()        
        GetSuperTypeOfType cenv.g cenv.amap range0 (generalizedTyconRef entity) 
        |> Option.map (fun ty -> FSharpType(cenv,  ty)) 
        
    member __.UsesPrefixDisplay = 
        if isUnresolved() then true else
        not (isResolvedAndFSharp()) || entity.Deref.IsPrefixDisplay

    member x.IsNamespace =  entity.IsNamespace
    member x.MembersOrValues =  x.MembersFunctionsAndValues
    member x.MembersFunctionsAndValues = 
      if isUnresolved() then makeReadOnlyCollection[] else
      protect <| fun () -> 
        ([ let _, entityTy = generalizeTyconRef entity
           if x.IsFSharpAbbreviation then 
               ()
           elif x.IsFSharp then 
               // For F# code we emit methods members in declaration order
               for v in entity.MembersOfFSharpTyconSorted do 
                 // Ignore members representing the generated .cctor
                 if not v.Deref.IsClassConstructor then 
                     let fsMeth = FSMeth (cenv.g, entityTy, v, None)
                     let item = 
                         if fsMeth.IsConstructor then  Item.CtorGroup (fsMeth.DisplayName, [fsMeth])                          
                         else Item.MethodGroup (fsMeth.DisplayName, [fsMeth])
                     yield FSharpMemberOrFunctionOrValue(cenv,  M fsMeth, item) 
           else
               for minfo in GetImmediateIntrinsicMethInfosOfType (None, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 entityTy do
                    yield FSharpMemberOrFunctionOrValue(cenv,  M minfo, Item.MethodGroup (minfo.DisplayName,[minfo]))
           let props = GetImmediateIntrinsicPropInfosOfType (None, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 entityTy 
           let events = cenv.infoReader.GetImmediateIntrinsicEventsOfType (None, AccessibleFromSomeFSharpCode, range0, entityTy)
           for pinfo in props do
                yield FSharpMemberOrFunctionOrValue(cenv, P pinfo, Item.Property (pinfo.PropertyName,[pinfo]))
           for einfo in events do
                yield FSharpMemberOrFunctionOrValue(cenv, E einfo, Item.Event einfo)

           // Emit the values, functions and F#-declared extension members in a module
           for v in entity.ModuleOrNamespaceType.AllValsAndMembers do
               if v.IsExtensionMember then

                   // For F#-declared extension members, yield a value-backed member and a property info if possible
                   let vref = mkNestedValRef entity v
                   yield FSharpMemberOrFunctionOrValue(cenv,  V vref, Item.Value vref) 
                   match v.MemberInfo.Value.MemberFlags.MemberKind, v.ApparentParent with
                   | MemberKind.PropertyGet, Parent p -> 
                        let pinfo = FSProp(cenv.g, generalizedTyconRef p, Some vref, None)
                        yield FSharpMemberOrFunctionOrValue(cenv,  P pinfo, Item.Property (pinfo.PropertyName, [pinfo]))
                   | MemberKind.PropertySet, Parent p -> 
                        let pinfo = FSProp(cenv.g, generalizedTyconRef p, None, Some vref)
                        yield FSharpMemberOrFunctionOrValue(cenv,  P pinfo, Item.Property (pinfo.PropertyName, [pinfo]))
                   | _ -> ()

               elif not v.IsMember then
                   let vref = mkNestedValRef entity v
                   yield FSharpMemberOrFunctionOrValue(cenv,  V vref, Item.Value vref) ]  
         |> makeReadOnlyCollection)
 
    member __.XmlDocSig = 
        checkIsResolved()
        getXmlDocSigForEntity cenv entity
 
    member __.XmlDoc = 
        if isUnresolved() then XmlDoc.Empty  |> makeXmlDoc else
        entity.XmlDoc |> makeXmlDoc

    member x.StaticParameters = 
        match entity.TypeReprInfo with 
        | TProvidedTypeExtensionPoint info -> 
            let m = x.DeclarationLocation
            let typeBeforeArguments = info.ProvidedType 
            let staticParameters = typeBeforeArguments.PApplyWithProvider((fun (typeBeforeArguments,provider) -> typeBeforeArguments.GetStaticParameters(provider)), range=m) 
            let staticParameters = staticParameters.PApplyArray(id, "GetStaticParameters", m)
            [| for p in staticParameters -> FSharpStaticParameter(cenv,  p, m) |]
        | _ -> [| |]
      |> makeReadOnlyCollection

    member __.NestedEntities = 
        if isUnresolved() then makeReadOnlyCollection[] else
        entity.ModuleOrNamespaceType.AllEntities 
        |> QueueList.toList
        |> List.map (fun x -> FSharpEntity(cenv,  entity.MkNestedTyconRef x))
        |> makeReadOnlyCollection

    member x.UnionCases = 
        if isUnresolved() then makeReadOnlyCollection[] else
        entity.UnionCasesAsRefList
        |> List.map (fun x -> FSharpUnionCase(cenv,  x)) 
        |> makeReadOnlyCollection

    member x.RecordFields = x.FSharpFields
    member x.FSharpFields =
        if isUnresolved() then makeReadOnlyCollection[] else

        entity.AllFieldsAsList
        |> List.map (fun x -> FSharpField(cenv,  mkRecdFieldRef entity x.Name))
        |> makeReadOnlyCollection

    member x.AbbreviatedType   = 
        checkIsResolved()

        match entity.TypeAbbrev with
        | None -> invalidOp "not a type abbreviation"
        | Some ty -> FSharpType(cenv,  ty)

    member __.Attributes = 
        if isUnresolved() then makeReadOnlyCollection[] else
        AttributeChecking.GetAttribInfosOfEntity cenv.g cenv.amap range0 entity
        |> List.map (fun a -> FSharpAttribute(cenv,  a))
        |> makeReadOnlyCollection

    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpEntity as otherEntity -> tyconRefEq cenv.g entity otherEntity.Entity
        |   _ -> false

    override x.GetHashCode() =
        checkIsResolved()
        ((hash entity.Stamp) <<< 1) + 1

    override x.ToString() = x.CompiledName

and FSharpUnionCase(cenv, v: UnionCaseRef) =
    inherit FSharpSymbol (cenv,   
                          (fun () -> 
                               checkEntityIsResolved v.TyconRef
                               Item.UnionCase(UnionCaseInfo(generalizeTypars v.TyconRef.TyparsNoRange,v))),
                          (fun _this thisCcu2 ad -> 
                               checkForCrossProjectAccessibility (thisCcu2, ad) (cenv.thisCcu, v.UnionCase.Accessibility)) 
                               //&& AccessibilityLogic.IsUnionCaseAccessible cenv.amap range0 ad v)
                               )


    let isUnresolved() = 
        entityIsUnresolved v.TyconRef || v.TryUnionCase.IsNone 
    let checkIsResolved() = 
        checkEntityIsResolved v.TyconRef
        if v.TryUnionCase.IsNone then 
            invalidOp (sprintf "The union case '%s' could not be found in the target type" v.CaseName)

    member __.IsUnresolved = 
        isUnresolved()

    member __.Name = 
        checkIsResolved()
        v.UnionCase.DisplayName

    member __.DeclarationLocation = 
        checkIsResolved()
        v.Range

    member __.UnionCaseFields = 
        if isUnresolved() then makeReadOnlyCollection [] else
        v.UnionCase.RecdFields |> List.mapi (fun i _ ->  FSharpField(cenv,  FSharpFieldData.Union (v, i))) |> List.toArray |> makeReadOnlyCollection

    member __.ReturnType = 
        checkIsResolved()
        FSharpType(cenv,  v.ReturnType)

    member __.CompiledName = 
        checkIsResolved()
        v.UnionCase.CompiledName

    member __.XmlDocSig = 
        checkIsResolved()
        let unionCase = UnionCaseInfo(generalizeTypars v.TyconRef.TyparsNoRange,v)
        match ItemDescriptionsImpl.GetXmlDocSigOfUnionCaseInfo unionCase with
        | Some (_, docsig) -> docsig
        | _ -> ""

    member __.XmlDoc = 
        if isUnresolved() then XmlDoc.Empty  |> makeXmlDoc else
        v.UnionCase.XmlDoc |> makeXmlDoc

    member __.Attributes = 
        if isUnresolved() then makeReadOnlyCollection [] else
        v.Attribs |> List.map (fun a -> FSharpAttribute(cenv, AttribInfo.FSAttribInfo(cenv.g, a))) |> makeReadOnlyCollection

    member __.Accessibility =  
        if isUnresolved() then FSharpAccessibility(taccessPublic) else
        FSharpAccessibility(v.UnionCase.Accessibility)

    member private x.V = v
    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpUnionCase as uc -> v === uc.V
        |   _ -> false
    
    override x.GetHashCode() = hash v.CaseName

    override x.ToString() = x.CompiledName


and FSharpFieldData = 
    | Recd of RecdFieldRef
    | Union of UnionCaseRef * int
    member x.RecdField =
        match x with 
        | Recd v -> v.RecdField
        | Union (v,n) -> v.FieldByIndex(n)
    member x.DeclaringTyconRef =
        match x with 
        | Recd v -> v.TyconRef
        | Union (v,_) -> v.TyconRef

and FSharpField(cenv, d: FSharpFieldData)  =
    inherit FSharpSymbol (cenv,  
                          (fun () -> 
                                match d with 
                                | Recd v -> 
                                    checkEntityIsResolved v.TyconRef
                                    Item.RecdField(RecdFieldInfo(generalizeTypars v.TyconRef.TyparsNoRange,v))
                                | Union (v,_) -> 
                                    // This is not correct: there is no "Item" for a named union case field
                                    Item.UnionCase(UnionCaseInfo(generalizeTypars v.TyconRef.TyparsNoRange,v))),
                          (fun this thisCcu2 ad -> 
                                checkForCrossProjectAccessibility (thisCcu2, ad) (cenv.thisCcu, (this :?> FSharpField).Accessibility.Contents)) 
                                //&&
                                //match d with 
                                //| Recd v -> AccessibilityLogic.IsRecdFieldAccessible cenv.amap range0 ad v
                                //| Union (v,_) -> AccessibilityLogic.IsUnionCaseAccessible cenv.amap range0 ad v)
                                )

    let isUnresolved() = 
        match d with 
        | Recd v -> entityIsUnresolved v.TyconRef || v.TryRecdField.IsNone 
        | Union (v,_) -> entityIsUnresolved v.TyconRef || v.TryUnionCase.IsNone 

    let checkIsResolved() = 
        match d with 
        | Recd v -> 
            checkEntityIsResolved v.TyconRef
            if v.TryRecdField.IsNone then 
                invalidOp (sprintf "The record field '%s' could not be found in the target type" v.FieldName)
        | Union (v,_) -> 
            checkEntityIsResolved v.TyconRef
            if v.TryUnionCase.IsNone then 
                invalidOp (sprintf "The union case '%s' could not be found in the target type" v.CaseName)

    new (cenv, ucref, n) = FSharpField(cenv, FSharpFieldData.Union(ucref,n))
    new (cenv, rfref) = FSharpField(cenv, FSharpFieldData.Recd(rfref))

    member __.DeclaringEntity = 
        FSharpEntity(cenv, d.DeclaringTyconRef)

    member __.IsUnresolved = 
        isUnresolved()

    member __.IsMutable = 
        if isUnresolved() then false else 
        d.RecdField.IsMutable

    member __.IsLiteral = 
        if isUnresolved() then false else 
        d.RecdField.LiteralValue.IsSome

    member __.LiteralValue = 
        if isUnresolved() then None else 
        match d.RecdField.LiteralValue with
        | Some lv ->
            match lv with
            | Const.Bool    v -> Some(box v)
            | Const.SByte   v -> Some(box v)
            | Const.Byte    v -> Some(box v)
            | Const.Int16   v -> Some(box v)
            | Const.UInt16  v -> Some(box v)
            | Const.Int32   v -> Some(box v)
            | Const.UInt32  v -> Some(box v)
            | Const.Int64   v -> Some(box v)
            | Const.UInt64  v -> Some(box v)
            | Const.IntPtr  v -> Some(box v)
            | Const.UIntPtr v -> Some(box v)
            | Const.Single  v -> Some(box v)
            | Const.Double  v -> Some(box v)
            | Const.Char    v -> Some(box v)
            | Const.String  v -> Some(box v)
            | Const.Decimal v -> Some(box v)
            | Const.Unit
            | Const.Zero      -> None
        | None -> None

    member __.IsVolatile = 
        if isUnresolved() then false else 
        d.RecdField.IsVolatile

    member __.IsDefaultValue = 
        if isUnresolved() then false else 
        d.RecdField.IsZeroInit

    member __.XmlDocSig = 
        checkIsResolved()
        let xmlsig =
            match d with 
            | Recd v -> 
                let recd = RecdFieldInfo(generalizeTypars v.TyconRef.TyparsNoRange,v)
                ItemDescriptionsImpl.GetXmlDocSigOfRecdFieldInfo recd
            | Union (v,_) -> 
                let unionCase = UnionCaseInfo(generalizeTypars v.TyconRef.TyparsNoRange,v)
                ItemDescriptionsImpl.GetXmlDocSigOfUnionCaseInfo unionCase
        match xmlsig with
        | Some (_, docsig) -> docsig
        | _ -> ""

    member __.XmlDoc = 
        if isUnresolved() then XmlDoc.Empty  |> makeXmlDoc else
        d.RecdField.XmlDoc |> makeXmlDoc

    member __.FieldType = 
        checkIsResolved()
        FSharpType(cenv,  d.RecdField.FormalType)

    member __.IsStatic = 
        if isUnresolved() then false else 
        d.RecdField.IsStatic

    member __.Name = 
        checkIsResolved()
        d.RecdField.Name

    member __.IsCompilerGenerated = 
        if isUnresolved() then false else 
        d.RecdField.IsCompilerGenerated

    member __.DeclarationLocation = 
        checkIsResolved()
        d.RecdField.Range

    member __.FieldAttributes = 
        if isUnresolved() then makeReadOnlyCollection [] else 
        d.RecdField.FieldAttribs |> List.map (fun a -> FSharpAttribute(cenv,  AttribInfo.FSAttribInfo(cenv.g, a))) |> makeReadOnlyCollection

    member __.PropertyAttributes = 
        if isUnresolved() then makeReadOnlyCollection [] else 
        d.RecdField.PropertyAttribs |> List.map (fun a -> FSharpAttribute(cenv,  AttribInfo.FSAttribInfo(cenv.g, a))) |> makeReadOnlyCollection

    member __.Accessibility : FSharpAccessibility =  
        if isUnresolved() then FSharpAccessibility(taccessPublic) else 
        FSharpAccessibility(d.RecdField.Accessibility) 

    member private x.V = d
    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpField as uc -> 
            match d, uc.V with 
            | Recd r1, Recd r2 -> recdFieldRefOrder.Compare(r1, r2) = 0
            | Union (u1,n1), Union (u2,n2) -> cenv.g.unionCaseRefEq u1 u2 && n1 = n2
            | _ -> false
        |   _ -> false

    override x.GetHashCode() = hash x.Name
    override x.ToString() = "field " + x.Name

and [<System.Obsolete("Renamed to FSharpField")>] FSharpRecordField = FSharpField

and FSharpAccessibility(a:Accessibility, ?isProtected) = 
    let isProtected = defaultArg isProtected  false

    let isInternalCompPath x = 
        match x with 
        | CompPath(ILScopeRef.Local,[]) -> true 
        | _ -> false

    let (|Public|Internal|Private|) (TAccess p) = 
        match p with 
        | [] -> Public 
        | _ when List.forall isInternalCompPath p  -> Internal 
        | _ -> Private

    member __.IsPublic = not isProtected && match a with Public -> true | _ -> false

    member __.IsPrivate = not isProtected && match a with Private -> true | _ -> false

    member __.IsInternal = not isProtected && match a with Internal -> true | _ -> false

    member __.IsProtected = isProtected

    member __.Contents = a

    override x.ToString() = stringOfAccess a

and [<Class>] FSharpAccessibilityRights(thisCcu: CcuThunk, ad:Infos.AccessorDomain) =
    member internal __.ThisCcu = thisCcu
    member internal __.Contents = ad


and FSharpActivePatternCase(cenv, apinfo: PrettyNaming.ActivePatternInfo, typ, n, valOpt: ValRef option, item) = 

    inherit FSharpSymbol (cenv,  
                          (fun () -> item),
                          (fun _ _ _ -> true))

    member __.Name = apinfo.ActiveTags.[n]

    member __.DeclarationLocation = snd apinfo.ActiveTagsWithRanges.[n]

    member __.Group = FSharpActivePatternGroup(cenv, apinfo, typ)

    member __.XmlDoc = 
        defaultArg (valOpt |> Option.map (fun vref -> vref.XmlDoc)) XmlDoc.Empty
        |> makeXmlDoc

    member __.XmlDocSig = 
        let xmlsig = 
            match valOpt with
            | Some valref -> ItemDescriptionsImpl.GetXmlDocSigOfActivePatternCase cenv.g valref
            | None -> None
        match xmlsig with
        | Some (_, docsig) -> docsig
        | _ -> ""

and FSharpActivePatternGroup(cenv, apinfo:PrettyNaming.ActivePatternInfo, typ) =
    
    member __.Names = makeReadOnlyCollection apinfo.Names

    member __.IsTotal = apinfo.IsTotal

    member __.OverallType = FSharpType(cenv, typ)

and FSharpGenericParameter(cenv, v:Typar) = 

    inherit FSharpSymbol (cenv,  
                          (fun () -> Item.TypeVar(v.Name, v)),
                          (fun _ _ _ad -> true))
    member __.Name = v.DisplayName
    member __.DeclarationLocation = v.Range
    member __.IsCompilerGenerated = v.IsCompilerGenerated
       
    member __.IsMeasure = (v.Kind = TyparKind.Measure)
    member __.XmlDoc = v.Data.typar_xmldoc |> makeXmlDoc
    member __.IsSolveAtCompileTime = (v.StaticReq = TyparStaticReq.HeadTypeStaticReq)
    member __.Attributes = 
         // INCOMPLETENESS: If the type parameter comes from .NET then the .NET metadata for the type parameter
         // has been lost (it is not accesible via Typar).  So we can't easily report the attributes in this 
         // case.
         v.Attribs |> List.map (fun a -> FSharpAttribute(cenv,  AttribInfo.FSAttribInfo(cenv.g, a))) |> makeReadOnlyCollection
    member __.Constraints = v.Constraints |> List.map (fun a -> FSharpGenericParameterConstraint(cenv, a)) |> makeReadOnlyCollection
    
    member internal x.V = v

    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpGenericParameter as p -> typarRefEq v p.V
        |   _ -> false

    override x.GetHashCode() = (hash v.Stamp)

    override x.ToString() = "generic parameter " + x.Name

and FSharpDelegateSignature(cenv, info : SlotSig) = 

    member __.DelegateArguments = 
        info.FormalParams.Head
        |> List.map (fun (TSlotParam(nm, ty, _, _, _, _)) -> nm, FSharpType(cenv,  ty))
        |> makeReadOnlyCollection

    member __.DelegateReturnType = 
        match info.FormalReturnType with
        | None -> FSharpType(cenv,  cenv.g.unit_ty)
        | Some ty -> FSharpType(cenv,  ty)
    override x.ToString() = "<delegate signature>"

and FSharpGenericParameterMemberConstraint(cenv, info : TraitConstraintInfo) = 
    let (TTrait(tys,nm,flags,atys,rty,_)) = info 
    member __.MemberSources = 
        tys   |> List.map (fun ty -> FSharpType(cenv,  ty)) |> makeReadOnlyCollection

    member __.MemberName = nm

    member __.MemberIsStatic = not flags.IsInstance

    member __.MemberArgumentTypes = atys   |> List.map (fun ty -> FSharpType(cenv,  ty)) |> makeReadOnlyCollection

    member x.MemberReturnType =
        match rty with 
        | None -> FSharpType(cenv,  cenv.g.unit_ty) 
        | Some ty -> FSharpType(cenv,  ty) 
    override x.ToString() = "<member constraint info>"


and FSharpGenericParameterDelegateConstraint(cenv, tupledArgTyp: TType, rty: TType) = 
    member __.DelegateTupledArgumentType = FSharpType(cenv,  tupledArgTyp)
    member __.DelegateReturnType =  FSharpType(cenv,  rty)
    override x.ToString() = "<delegate constraint info>"

and FSharpGenericParameterDefaultsToConstraint(cenv, pri:int, ty:TType) = 
    member __.DefaultsToPriority = pri 
    member __.DefaultsToTarget = FSharpType(cenv,  ty) 
    override x.ToString() = "<defaults-to constraint info>"

and FSharpGenericParameterConstraint(cenv, cx : TyparConstraint) = 

    member __.IsCoercesToConstraint = 
        match cx with 
        | TyparConstraint.CoercesTo _ -> true 
        | _ -> false

    member __.CoercesToTarget = 
        match cx with 
        | TyparConstraint.CoercesTo(ty,_) -> FSharpType(cenv,  ty) 
        | _ -> invalidOp "not a coerces-to constraint"

    member __.IsDefaultsToConstraint = 
        match cx with 
        | TyparConstraint.DefaultsTo _ -> true 
        | _ -> false

    member __.DefaultsToConstraintData = 
        match cx with 
        | TyparConstraint.DefaultsTo(pri, ty, _) ->  FSharpGenericParameterDefaultsToConstraint(cenv,  pri, ty) 
        | _ -> invalidOp "not a 'defaults-to' constraint"

    member __.IsSupportsNullConstraint  = match cx with TyparConstraint.SupportsNull _ -> true | _ -> false

    member __.IsMemberConstraint = 
        match cx with 
        | TyparConstraint.MayResolveMember _ -> true 
        | _ -> false

    member __.MemberConstraintData =  
        match cx with 
        | TyparConstraint.MayResolveMember(info, _) ->  FSharpGenericParameterMemberConstraint(cenv,  info) 
        | _ -> invalidOp "not a member constraint"

    member __.IsNonNullableValueTypeConstraint = 
        match cx with 
        | TyparConstraint.IsNonNullableStruct _ -> true 
        | _ -> false
    
    member __.IsReferenceTypeConstraint  = 
        match cx with 
        | TyparConstraint.IsReferenceType _ -> true 
        | _ -> false

    member __.IsSimpleChoiceConstraint = 
        match cx with 
        | TyparConstraint.SimpleChoice _ -> true 
        | _ -> false

    member __.SimpleChoices = 
        match cx with 
        | TyparConstraint.SimpleChoice (tys,_) -> 
            tys   |> List.map (fun ty -> FSharpType(cenv,  ty)) |> makeReadOnlyCollection
        | _ -> invalidOp "incorrect constraint kind"

    member __.IsRequiresDefaultConstructorConstraint  = 
        match cx with 
        | TyparConstraint.RequiresDefaultConstructor _ -> true 
        | _ -> false

    member __.IsEnumConstraint = 
        match cx with 
        | TyparConstraint.IsEnum _ -> true 
        | _ -> false

    member __.EnumConstraintTarget = 
        match cx with 
        | TyparConstraint.IsEnum(ty,_) -> FSharpType(cenv,  ty)
        | _ -> invalidOp "incorrect constraint kind"
    
    member __.IsComparisonConstraint = 
        match cx with 
        | TyparConstraint.SupportsComparison _ -> true 
        | _ -> false

    member __.IsEqualityConstraint = 
        match cx with 
        | TyparConstraint.SupportsEquality _ -> true 
        | _ -> false

    member __.IsUnmanagedConstraint = 
        match cx with 
        | TyparConstraint.IsUnmanaged _ -> true 
        | _ -> false

    member __.IsDelegateConstraint = 
        match cx with 
        | TyparConstraint.IsDelegate _ -> true 
        | _ -> false

    member __.DelegateConstraintData =  
        match cx with 
        | TyparConstraint.IsDelegate(ty1,ty2, _) ->  FSharpGenericParameterDelegateConstraint(cenv,  ty1, ty2) 
        | _ -> invalidOp "not a delegate constraint"

    override x.ToString() = "<type constraint>"

and FSharpInlineAnnotation = 
   | PseudoValue
   | AlwaysInline 
   | OptionalInline 
   | NeverInline 

and FSharpMemberOrValData = 
    | E of EventInfo
    | P of PropInfo
    | M of MethInfo
    | V of ValRef

and FSharpMemberOrVal = FSharpMemberOrFunctionOrValue

and FSharpMemberFunctionOrValue =  FSharpMemberOrFunctionOrValue

and FSharpMemberOrFunctionOrValue(cenv, d:FSharpMemberOrValData, item) = 

    inherit FSharpSymbol(cenv,  
                         (fun () -> item),
                         (fun this thisCcu2 ad -> 
                              let this = this :?> FSharpMemberOrFunctionOrValue 
                              checkForCrossProjectAccessibility (thisCcu2, ad) (cenv.thisCcu, this.Accessibility.Contents)) 
                              //&& 
                              //match d with 
                              //| E e -> 
                              //    match e with 
                              //    | EventInfo.ILEvent (_,e) -> AccessibilityLogic.IsILEventInfoAccessible g cenv.amap range0 ad e
                              //    | EventInfo.FSEvent (_,_,vref,_) ->  AccessibilityLogic.IsValAccessible ad vref
                              //    | _ -> true
                              //| M m -> AccessibilityLogic.IsMethInfoAccessible cenv.amap range0 ad m
                              //| P p -> AccessibilityLogic.IsPropInfoAccessible g cenv.amap range0 ad p
                              //| V v -> AccessibilityLogic.IsValAccessible ad v
                          )

    let fsharpInfo() = 
        match d with 
        | M m -> m.ArbitraryValRef 
        | P p -> p.ArbitraryValRef 
        | E e -> e.ArbitraryValRef 
        | V v -> Some v
    
    let isUnresolved() = 
        match fsharpInfo() with 
        | None -> false
        | Some v -> v.TryDeref.IsNone

    let checkIsResolved() = 
        if isUnresolved() then 
            let v = (fsharpInfo()).Value
            let nm = (match v with VRefNonLocal n -> n.ItemKey.PartialKey.LogicalName | _ -> "<local>")
            invalidOp (sprintf "The value or member '%s' does not exist or is in an unresolved assembly." nm)

    new (cenv, vref) = FSharpMemberFunctionOrValue(cenv, V vref, Item.Value vref)
    new (cenv, minfo) =  FSharpMemberFunctionOrValue(cenv, M minfo, Item.MethodGroup(minfo.LogicalName, [minfo]))

    member __.IsUnresolved = 
        isUnresolved()

    member __.DeclarationLocationOpt = 
        checkIsResolved()
        match fsharpInfo() with 
        | Some v -> Some v.Range
        | None -> base.DeclarationLocation 

    member x.Overloads =
        checkIsResolved()
        match d with
        | M m ->
            match item with
            | Item.MethodGroup (_name, methodInfos) -> 
                methodInfos
                |> List.filter (fun methodInfo -> not (methodInfo.NumArgs = m.NumArgs) )
                |> List.map (fun mi -> FSharpMemberOrFunctionOrValue(cenv, M mi, item))
                |> Some
            | _ -> None
        | _ -> None

    member x.DeclarationLocation = 
        checkIsResolved()
        match x.DeclarationLocationOpt with 
        | Some v -> v
        | None -> failwith "DeclarationLocation property not available"

    member __.LogicalEnclosingEntity = 
        checkIsResolved()
        match d with 
        | E m -> FSharpEntity(cenv,  tcrefOfAppTy cenv.g m.EnclosingType)
        | P m -> FSharpEntity(cenv,  tcrefOfAppTy cenv.g m.EnclosingType)
        | M m -> FSharpEntity(cenv,  tcrefOfAppTy cenv.g m.EnclosingType)
        | V v -> 
        match v.ApparentParent with 
        | ParentNone -> invalidOp "the value or member doesn't have a logical parent" 
        | Parent p -> FSharpEntity(cenv,  p)

    member x.GenericParameters = 
        checkIsResolved()
        let tps = 
            match d with 
            | E _ -> []
            | P _ -> []
            | M m -> m.FormalMethodTypars
            | V v -> v.Typars 
        tps |> List.map (fun tp -> FSharpGenericParameter(cenv,  tp)) |> List.toArray |> makeReadOnlyCollection

    member x.FullType = 
        checkIsResolved()
        let ty = 
            match d with 
            | E e -> e.GetDelegateType(cenv.amap,range0)
            | P p -> p.GetPropertyType(cenv.amap,range0)
            | M m -> 
                let rty = m.GetFSharpReturnTy(cenv.amap,range0,m.FormalMethodInst)
                let argtysl = m.GetParamTypes(cenv.amap,range0,m.FormalMethodInst) 
                mkIteratedFunTy (List.map (mkTupledTy cenv.g) argtysl) rty
            | V v -> v.TauType
        FSharpType(cenv,  ty)

    member __.HasGetterMethod =
        if isUnresolved() then false
        else
            match d with 
            | P m -> m.HasGetter
            | E _
            | M _
            | V _ -> false

    member __.GetterMethod =
        checkIsResolved()
        match d with 
        | P m -> 
            let minfo = m.GetterMethod
            FSharpMemberOrFunctionOrValue(cenv, M minfo, Item.MethodGroup (minfo.DisplayName,[minfo]))
        | E _
        | M _
        | V _ -> invalidOp "the value or member doesn't have an associated getter method" 

    member __.HasSetterMethod =
        if isUnresolved() then false
        else
            match d with 
            | P m -> m.HasSetter
            | E _
            | M _
            | V _ -> false

    member __.SetterMethod =
        checkIsResolved()
        match d with 
        | P m -> 
            let minfo = m.SetterMethod
            FSharpMemberOrFunctionOrValue(cenv, M minfo, Item.MethodGroup (minfo.DisplayName,[minfo]))
        | E _
        | M _
        | V _ -> invalidOp "the value or member doesn't have an associated setter method" 

    member __.EnclosingEntity = 
        checkIsResolved()
        match d with 
        | E m -> FSharpEntity(cenv,  tcrefOfAppTy cenv.g m.EnclosingType)
        | P m -> FSharpEntity(cenv,  tcrefOfAppTy cenv.g m.EnclosingType)
        | M m -> FSharpEntity(cenv,  m.DeclaringEntityRef)
        | V v -> 
        match v.ActualParent with 
        | ParentNone -> invalidOp "the value or member doesn't have an enclosing entity" 
        | Parent p -> FSharpEntity(cenv,  p)

    member __.IsCompilerGenerated = 
        if isUnresolved() then false else 
        match fsharpInfo() with 
        | None -> false
        | Some v -> 
        v.IsCompilerGenerated

    member __.InlineAnnotation = 
        if isUnresolved() then FSharpInlineAnnotation.OptionalInline else 
        match fsharpInfo() with 
        | None -> FSharpInlineAnnotation.OptionalInline
        | Some v -> 
        match v.InlineInfo with 
        | ValInline.PseudoVal -> FSharpInlineAnnotation.PseudoValue
        | ValInline.Always -> FSharpInlineAnnotation.AlwaysInline
        | ValInline.Optional -> FSharpInlineAnnotation.OptionalInline
        | ValInline.Never -> FSharpInlineAnnotation.NeverInline

    member __.IsMutable = 
        if isUnresolved() then false else 
        match d with 
        | M _ | P _ |  E _ -> false
        | V v -> v.IsMutable

    member __.IsModuleValueOrMember = 
        if isUnresolved() then false else 
        match d with 
        | M _ | P _ | E _ -> true
        | V v -> v.IsMember || v.IsModuleBinding

    member __.IsMember = 
        if isUnresolved() then false else 
        match d with 
        | M _ | P _ | E _ -> true
        | V v -> v.IsMember 
    
    member __.IsDispatchSlot = 
        if isUnresolved() then false else 
        match d with 
        | E e -> e.GetAddMethod().IsDispatchSlot
        | P p -> p.IsDispatchSlot
        | M m -> m.IsDispatchSlot
        | V v -> v.IsDispatchSlot

    member x.IsProperty = 
        match d with 
        | P _ -> true
        | _ -> false

    member x.IsEvent = 
        match d with 
        | E _ -> true
        | _ -> false

    member __.IsEventAddMethod = 
        if isUnresolved() then false else 
        match d with 
        | M m when m.LogicalName.StartsWith("add_") -> 
            let eventName = m.LogicalName.[4..]
            let entityTy = generalizedTyconRef m.DeclaringEntityRef
            nonNil (cenv.infoReader.GetImmediateIntrinsicEventsOfType (Some eventName, AccessibleFromSomeFSharpCode, range0, entityTy)) ||
            match GetImmediateIntrinsicPropInfosOfType(Some eventName, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 (generalizedTyconRef m.DeclaringEntityRef) with 
            | pinfo :: _  -> pinfo.IsFSharpEventProperty
            | _ -> false

        | _ -> false

    member __.IsEventRemoveMethod = 
        if isUnresolved() then false else 
        match d with 
        | M m when m.LogicalName.StartsWith("remove_") -> 
            let eventName = m.LogicalName.[7..]
            let entityTy = generalizedTyconRef m.DeclaringEntityRef
            nonNil (cenv.infoReader.GetImmediateIntrinsicEventsOfType (Some eventName, AccessibleFromSomeFSharpCode, range0, entityTy)) ||
            match GetImmediateIntrinsicPropInfosOfType(Some eventName, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 (generalizedTyconRef m.DeclaringEntityRef) with 
            | pinfo :: _ -> pinfo.IsFSharpEventProperty
            | _ -> false
        | _ -> false

    member x.IsGetterMethod =  
        if isUnresolved() then false else 
        x.IsPropertyGetterMethod ||
        match fsharpInfo() with 
        | None -> false
        | Some v -> 
        match v.MemberInfo with 
        | None -> false 
        | Some memInfo -> memInfo.MemberFlags.MemberKind = MemberKind.PropertyGet

    member x.IsSetterMethod =  
        if isUnresolved() then false else 
        x.IsPropertySetterMethod ||
        match fsharpInfo() with 
        | None -> false
        | Some v -> 
        match v.MemberInfo with 
        | None -> false 
        | Some memInfo -> memInfo.MemberFlags.MemberKind = MemberKind.PropertySet

    member __.IsPropertyGetterMethod = 
        if isUnresolved() then false else 
        match d with 
        | M m when m.LogicalName.StartsWith("get_") -> 
            let propName = PrettyNaming.ChopPropertyName(m.LogicalName) 
            nonNil (GetImmediateIntrinsicPropInfosOfType(Some propName, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 (generalizedTyconRef m.DeclaringEntityRef))
        | V v -> 
            match v.MemberInfo with 
            | None -> false 
            | Some memInfo -> memInfo.MemberFlags.MemberKind = MemberKind.PropertyGet
        | _ -> false

    member __.IsPropertySetterMethod = 
        if isUnresolved() then false else 
        match d with 
        // Look for a matching property with the right name. 
        | M m when m.LogicalName.StartsWith("set_") -> 
            let propName = PrettyNaming.ChopPropertyName(m.LogicalName) 
            nonNil (GetImmediateIntrinsicPropInfosOfType(Some propName, AccessibleFromSomeFSharpCode) cenv.g cenv.amap range0 (generalizedTyconRef m.DeclaringEntityRef))
        | V v -> 
            match v.MemberInfo with 
            | None -> false 
            | Some memInfo -> memInfo.MemberFlags.MemberKind = MemberKind.PropertySet
        | _ -> false

    member __.IsInstanceMember = 
        if isUnresolved() then false else 
        match d with 
        | E e -> not e.IsStatic
        | P p -> not p.IsStatic
        | M m -> m.IsInstance
        | V v -> v.IsInstanceMember

    member __.IsExtensionMember = 
        if isUnresolved() then false else 
        match d with 
        | E e -> e.GetAddMethod().IsExtensionMember
        | P p -> p.IsExtensionMember
        | M m -> m.IsExtensionMember
        | V v -> v.IsExtensionMember

    member this.IsOverrideOrExplicitMember = this.IsOverrideOrExplicitInterfaceImplementation
    member __.IsOverrideOrExplicitInterfaceImplementation =
        if isUnresolved() then false else 
        match d with 
        | E e -> e.GetAddMethod().IsDefiniteFSharpOverride
        | P p -> p.IsDefiniteFSharpOverride
        | M m -> m.IsDefiniteFSharpOverride
        | V v -> 
            v.MemberInfo.IsSome && v.IsDefiniteFSharpOverrideMember

    member __.IsExplicitInterfaceImplementation =
        if isUnresolved() then false else 
        match d with 
        | E e -> e.GetAddMethod().IsFSharpExplicitInterfaceImplementation
        | P p -> p.IsFSharpExplicitInterfaceImplementation
        | M m -> m.IsFSharpExplicitInterfaceImplementation
        | V v -> v.IsFSharpExplicitInterfaceImplementation cenv.g

    member __.IsImplicitConstructor = 
        if isUnresolved() then false else 
        match fsharpInfo() with 
        | None -> false
        | Some v -> v.IsIncrClassConstructor
    
    member __.IsTypeFunction = 
        if isUnresolved() then false else 
        match fsharpInfo() with 
        | None -> false
        | Some v -> v.IsTypeFunction

    member __.IsActivePattern =  
        if isUnresolved() then false else 
        match fsharpInfo() with 
        | Some v -> PrettyNaming.ActivePatternInfoOfValName v.CoreDisplayName v.Range |> isSome
        | None -> false

    member x.CompiledName = 
        checkIsResolved()
        match fsharpInfo() with 
        | Some v -> v.CompiledName
        | None -> x.LogicalName

    member __.LogicalName = 
        checkIsResolved()
        match d with 
        | E e -> e.EventName
        | P p -> p.PropertyName
        | M m -> m.LogicalName
        | V v -> v.LogicalName

    member __.DisplayName = 
        checkIsResolved()
        match d with 
        | E e -> e.EventName
        | P p -> p.PropertyName
        | M m -> m.DisplayName
        | V v -> v.DisplayName

    member __.XmlDocSig = 
        checkIsResolved()
 
        match d with 
        | E e ->
            let range = defaultArg __.DeclarationLocationOpt range0
            match ItemDescriptionsImpl.GetXmlDocSigOfEvent cenv.infoReader range e with
            | Some (_, docsig) -> docsig
            | _ -> ""
        | P p ->
            let range = defaultArg __.DeclarationLocationOpt range0
            match ItemDescriptionsImpl.GetXmlDocSigOfProp cenv.infoReader range p with
            | Some (_, docsig) -> docsig
            | _ -> ""
        | M m -> 
            let range = defaultArg __.DeclarationLocationOpt range0
            match ItemDescriptionsImpl.GetXmlDocSigOfMethInfo cenv.infoReader range m with
            | Some (_, docsig) -> docsig
            | _ -> ""
        | V v ->
            match v.ActualParent with 
            | Parent entityRef -> 
                match ItemDescriptionsImpl.GetXmlDocSigOfValRef cenv.g entityRef v with
                | Some (_, docsig) -> docsig
                | _ -> ""
            | ParentNone -> "" 

    member __.XmlDoc = 
        if isUnresolved() then XmlDoc.Empty  |> makeXmlDoc else
        match d with 
        | E e -> e.XmlDoc |> makeXmlDoc
        | P p -> p.XmlDoc |> makeXmlDoc
        | M m -> m.XmlDoc |> makeXmlDoc
        | V v -> v.XmlDoc |> makeXmlDoc

    member x.CurriedParameterGroups = 
        checkIsResolved()
        match d with 
        | P p -> 
            
            [ [ for (ParamData(isParamArrayArg,isOutArg,optArgInfo,nmOpt,pty)) in p.GetParamDatas(cenv.amap,range0) do 
                // INCOMPLETENESS: Attribs is empty here, so we can't look at attributes for
                // either .NET or F# parameters
                let argInfo : ArgReprInfo = { Name=nmOpt; Attribs= [] }
                yield FSharpParameter(cenv,  pty, argInfo, x.DeclarationLocationOpt, isParamArrayArg, isOutArg, optArgInfo.IsOptional) ] 
               |> makeReadOnlyCollection  ]
           |> makeReadOnlyCollection

        | E _ ->  []  |> makeReadOnlyCollection
        | M m -> 
            
            [ for argtys in m.GetParamDatas(cenv.amap,range0,m.FormalMethodInst) do 
                 yield 
                   [ for (ParamData(isParamArrayArg,isOutArg,optArgInfo,nmOpt,pty)) in argtys do 
                // INCOMPLETENESS: Attribs is empty here, so we can't look at attributes for
                // either .NET or F# parameters
                        let argInfo : ArgReprInfo = { Name=nmOpt; Attribs= [] }
                        yield FSharpParameter(cenv,  pty, argInfo, x.DeclarationLocationOpt, isParamArrayArg, isOutArg, optArgInfo.IsOptional) ] 
                   |> makeReadOnlyCollection ]
             |> makeReadOnlyCollection

        | V v -> 
        match v.ValReprInfo with 
        | None -> failwith "not a module let binding or member"
        | Some (ValReprInfo(_typars,curriedArgInfos,_retInfo)) -> 
            let tau = v.TauType
            let argtysl,_ = GetTopTauTypeInFSharpForm cenv.g curriedArgInfos tau range0
            let argtysl = if v.IsInstanceMember then argtysl.Tail else argtysl
            
            [ for argtys in argtysl do 
                 yield 
                   [ for argty, argInfo in argtys do 
                        let isParamArrayArg = HasFSharpAttribute cenv.g cenv.g.attrib_ParamArrayAttribute argInfo.Attribs
                        let isOutArg = HasFSharpAttribute cenv.g cenv.g.attrib_OutAttribute argInfo.Attribs && isByrefTy cenv.g argty
                        let isOptionalArg = HasFSharpAttribute cenv.g cenv.g.attrib_OptionalArgumentAttribute argInfo.Attribs
                        yield FSharpParameter(cenv,  argty, argInfo, x.DeclarationLocationOpt, isParamArrayArg, isOutArg, isOptionalArg) ] 
                   |> makeReadOnlyCollection ]
             |> makeReadOnlyCollection

    member x.ReturnParameter = 
        checkIsResolved()
        match d with 
        | E e -> 
                // INCOMPLETENESS: Attribs is empty here, so we can't look at return attributes for .NET or F# methods
            let retInfo : ArgReprInfo = { Name=None; Attribs= [] }
            let rty = PropTypOfEventInfo cenv.infoReader range0 AccessibleFromSomewhere e
            let _,rty, _cxs = PrettyTypes.PrettifyTypes1 cenv.g rty
            FSharpParameter(cenv,  rty, retInfo, x.DeclarationLocationOpt, isParamArrayArg=false, isOutArg=false, isOptionalArg=false) 
        | P p -> 
                // INCOMPLETENESS: Attribs is empty here, so we can't look at return attributes for .NET or F# methods
            let retInfo : ArgReprInfo = { Name=None; Attribs= [] }
            let rty = p.GetPropertyType(cenv.amap,range0)
            FSharpParameter(cenv,  rty, retInfo, x.DeclarationLocationOpt, isParamArrayArg=false, isOutArg=false, isOptionalArg=false) 
        | M m -> 
                // INCOMPLETENESS: Attribs is empty here, so we can't look at return attributes for .NET or F# methods
            let retInfo : ArgReprInfo = { Name=None; Attribs= [] }
            let rty = m.GetFSharpReturnTy(cenv.amap,range0,m.FormalMethodInst)
            FSharpParameter(cenv,  rty, retInfo, x.DeclarationLocationOpt, isParamArrayArg=false, isOutArg=false, isOptionalArg=false) 
        | V v -> 
        match v.ValReprInfo with 
        | None -> failwith "not a module let binding or member" 
        | Some (ValReprInfo(_typars,argInfos,retInfo)) -> 
        
            let tau = v.TauType
            let _,rty = GetTopTauTypeInFSharpForm cenv.g argInfos tau range0
            
            FSharpParameter(cenv,  rty, retInfo, x.DeclarationLocationOpt, isParamArrayArg=false, isOutArg=false, isOptionalArg=false) 


    member __.Attributes = 
        if isUnresolved() then makeReadOnlyCollection [] else 
        let m = range0
        match d with 
        | E einfo -> 
            AttributeChecking.GetAttribInfosOfEvent cenv.amap m einfo |> List.map (fun a -> FSharpAttribute(cenv,  a))
        | P pinfo -> 
            AttributeChecking.GetAttribInfosOfProp cenv.amap m pinfo |> List.map (fun a -> FSharpAttribute(cenv,  a))
        | M minfo -> 
            AttributeChecking.GetAttribInfosOfMethod cenv.amap m minfo |> List.map (fun a -> FSharpAttribute(cenv,  a))
        | V v -> 
            v.Attribs |> List.map (fun a -> FSharpAttribute(cenv,  AttribInfo.FSAttribInfo(cenv.g, a))) 
     |> makeReadOnlyCollection
     
(*
    /// Is this "base" in "base.M(...)"
    member __.IsBaseValue : bool

    /// Is this the "x" in "type C() as x = ..."
    member __.IsConstructorThisValue : bool

    /// Is this the "x" in "member __.M = ..."
    member __.IsMemberThisValue : bool

    /// Is this a [<Literal>] value, and if so what value?
    member __.LiteralValue : obj // may be null

*)

      /// How visible is this? 
    member this.Accessibility : FSharpAccessibility  = 
        if isUnresolved() then FSharpAccessibility(taccessPublic) else 
        match fsharpInfo() with 
        | Some v -> FSharpAccessibility(v.Accessibility)
        | None ->  
        
        // Note, returning "public" is wrong for IL members that are private
        match d with 
        | E e ->  
            // For IL events, we get an approximate accessiblity that at least reports "internal" as "internal" and "private" as "private"
            let access = 
                match e with 
                | ILEvent (_,x) -> 
                    let ilAccess = AccessibilityLogic.GetILAccessOfILEventInfo x
                    getApproxFSharpAccessibilityOfMember this.EnclosingEntity.Entity  ilAccess
                | _ -> taccessPublic

            FSharpAccessibility(access)

        | P p ->  
            // For IL  properties, we get an approximate accessiblity that at least reports "internal" as "internal" and "private" as "private"
            let access = 
                match p with 
                | ILProp (_,x) -> 
                    let ilAccess = AccessibilityLogic.GetILAccessOfILPropInfo x
                    getApproxFSharpAccessibilityOfMember this.EnclosingEntity.Entity  ilAccess
                | _ -> taccessPublic

            FSharpAccessibility(access)

        | M m ->  

            // For IL  methods, we get an approximate accessiblity that at least reports "internal" as "internal" and "private" as "private"
            let access = 
                match m with 
                | ILMeth (_,x,_) -> getApproxFSharpAccessibilityOfMember x.DeclaringTyconRef x.RawMetadata.Access 
                | _ -> taccessPublic

            FSharpAccessibility(access,isProtected=m.IsProtectedAccessiblity)

        | V v -> FSharpAccessibility(v.Accessibility)

    member x.Data = d

    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpMemberOrFunctionOrValue as other ->
            match d, other.Data with 
            | E evt1, E evt2 -> EventInfo.EventInfosUseIdenticalDefintions evt1 evt2 
            | P p1, P p2 ->  PropInfo.PropInfosUseIdenticalDefinitions p1 p2
            | M m1, M m2 ->  MethInfo.MethInfosUseIdenticalDefinitions m1 m2
            | V v1, V v2 -> valRefEq cenv.g v1 v2
            | _ -> false
        |   _ -> false

    override x.GetHashCode() = hash (box x.LogicalName)
    override x.ToString() = 
        try  
            let prefix = (if x.IsEvent then "event " elif x.IsProperty then "property " elif x.IsMember then "member " else "val ") 
            prefix + x.LogicalName 
        with _  -> "??"


and FSharpType(cenv, typ:TType) =

    let isUnresolved() = 
       ErrorLogger.protectAssemblyExploration true <| fun () -> 
        match stripTyparEqns typ with 
        | TType_app (tcref,_) -> FSharpEntity(cenv,  tcref).IsUnresolved
        | TType_measure (MeasureCon tcref) ->  FSharpEntity(cenv,  tcref).IsUnresolved
        | TType_measure (MeasureProd _) ->  FSharpEntity(cenv,  cenv.g.measureproduct_tcr).IsUnresolved 
        | TType_measure MeasureOne ->  FSharpEntity(cenv,  cenv.g.measureone_tcr).IsUnresolved 
        | TType_measure (MeasureInv _) ->  FSharpEntity(cenv,  cenv.g.measureinverse_tcr).IsUnresolved 
        | _ -> false
    
    let isResolved() = not (isUnresolved())

    new (g, thisCcu, tcImports, typ) = FSharpType(cenv(g,thisCcu,tcImports), typ)

    member __.IsUnresolved = isUnresolved()

    member __.HasTypeDefinition = 
       isResolved() &&
       protect <| fun () -> 
         match stripTyparEqns typ with 
         | TType_app _ | TType_measure (MeasureCon _ | MeasureProd _ | MeasureInv _ | MeasureOne _) -> true 
         | _ -> false

    member __.IsTupleType = 
       isResolved() &&
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_tuple _ -> true 
        | _ -> false

    member x.IsNamedType = x.HasTypeDefinition
    member x.NamedEntity = x.TypeDefinition

    member __.TypeDefinition = 
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_app (tcref,_) -> FSharpEntity(cenv,  tcref) 
        | TType_measure (MeasureCon tcref) ->  FSharpEntity(cenv,  tcref) 
        | TType_measure (MeasureProd _) ->  FSharpEntity(cenv,  cenv.g.measureproduct_tcr) 
        | TType_measure MeasureOne ->  FSharpEntity(cenv,  cenv.g.measureone_tcr) 
        | TType_measure (MeasureInv _) ->  FSharpEntity(cenv,  cenv.g.measureinverse_tcr) 
        | _ -> invalidOp "not a named type"

    member __.GenericArguments = 
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_app (_,tyargs) 
        | TType_tuple (tyargs) -> (tyargs |> List.map (fun ty -> FSharpType(cenv,  ty)) |> makeReadOnlyCollection) 
        | TType_fun(d,r) -> [| FSharpType(cenv,  d); FSharpType(cenv,  r) |] |> makeReadOnlyCollection
        | TType_measure (MeasureCon _) ->  [| |] |> makeReadOnlyCollection
        | TType_measure (MeasureProd (t1,t2)) ->  [| FSharpType(cenv,  TType_measure t1); FSharpType(cenv,  TType_measure t2) |] |> makeReadOnlyCollection
        | TType_measure MeasureOne ->  [| |] |> makeReadOnlyCollection
        | TType_measure (MeasureInv t1) ->  [| FSharpType(cenv,  TType_measure t1) |] |> makeReadOnlyCollection
        | _ -> invalidOp "not a named type"

(*
    member __.ProvidedArguments = 
        let typeName, argNamesAndValues = 
            try 
                PrettyNaming.demangleProvidedTypeName typeLogicalName 
            with PrettyNaming.InvalidMangledStaticArg piece -> 
                error(Error(FSComp.SR.etProvidedTypeReferenceInvalidText(piece),range0)) 
*)

    member typ.IsAbbreviation = 
       isResolved() && typ.HasTypeDefinition && typ.TypeDefinition.IsFSharpAbbreviation

    member __.AbbreviatedType = 
       protect <| fun () -> FSharpType(cenv,  stripTyEqns cenv.g typ)

    member __.IsFunctionType = 
       isResolved() &&
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_fun _ -> true 
        | _ -> false

    member __.IsGenericParameter = 
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_var _ -> true 
        | TType_measure (MeasureVar _) -> true 
        | _ -> false

    member __.GenericParameter = 
       protect <| fun () -> 
        match stripTyparEqns typ with 
        | TType_var tp 
        | TType_measure (MeasureVar tp) -> 
            FSharpGenericParameter (cenv,  tp)
        | _ -> invalidOp "not a generic parameter type"

    member x.AllInterfaces = 
        if isUnresolved() then makeReadOnlyCollection [] else
        [ for ty in AllInterfacesOfType  cenv.g cenv.amap range0 AllowMultiIntfInstantiations.Yes typ do 
             yield FSharpType(cenv, ty) ]
        |> makeReadOnlyCollection

    member x.BaseType = 
        GetSuperTypeOfType cenv.g cenv.amap range0 typ
        |> Option.map (fun ty -> FSharpType(cenv, ty)) 

    member x.Instantiate(tys:(FSharpGenericParameter * FSharpType) list) = 
        let typI = instType (tys |> List.map (fun (tyv,typ) -> tyv.V, typ.Typ)) typ
        FSharpType(cenv, typI)

    member private x.Typ = typ

    override x.Equals(other : obj) =
        box x === other ||
        match other with
        |   :? FSharpType as t -> typeEquiv cenv.g typ t.Typ
        |   _ -> false

    override x.GetHashCode() = hash x

    member x.Format(denv: FSharpDisplayContext) = 
       protect <| fun () -> 
        NicePrint.stringOfTy (denv.Contents cenv.g) typ 

    override x.ToString() = 
       protect <| fun () -> 
        "type " + NicePrint.stringOfTy (DisplayEnv.Empty(cenv.g)) typ 

and FSharpAttribute(cenv: cenv, attrib: AttribInfo) = 

    member __.AttributeType =  
        FSharpEntity(cenv,  attrib.TyconRef)

    member __.IsUnresolved = entityIsUnresolved(attrib.TyconRef)

    member __.ConstructorArguments = 
        attrib.ConstructorArguments 
        |> List.map (fun (ty, obj) -> FSharpType(cenv, ty), obj)
        |> makeReadOnlyCollection

    member __.NamedArguments = 
        attrib.NamedArguments 
        |> List.map (fun (ty, nm, isField, obj) -> FSharpType(cenv, ty), nm, isField, obj)
        |> makeReadOnlyCollection

    member __.Format(denv: FSharpDisplayContext) = 
        protect <| fun () -> 
            match attrib with
            | AttribInfo.FSAttribInfo(g, attrib) ->
                NicePrint.stringOfFSAttrib (denv.Contents g) attrib
            | AttribInfo.ILAttribInfo (g, _, scoref, cattr, _) -> 
                let parms, _args = decodeILAttribData g.ilg cattr (Some scoref) 
                NicePrint.stringOfILAttrib (denv.Contents g) (cattr.Method.EnclosingType, parms)

    override __.ToString() = 
        if entityIsUnresolved attrib.TyconRef then "attribute ???" else "attribute " + attrib.TyconRef.CompiledName + "(...)" 
    
and FSharpStaticParameter(cenv,  sp: Tainted< ExtensionTyping.ProvidedParameterInfo >, m) = 
    inherit FSharpSymbol(cenv,  
                         (fun () -> 
                              protect <| fun () -> 
                                let spKind = Import.ImportProvidedType cenv.amap m (sp.PApply((fun x -> x.ParameterType), m))
                                let nm = sp.PUntaint((fun p -> p.Name), m)
                                Item.ArgName((mkSynId m nm, spKind, None))),
                         (fun _ _ _ -> true))

    member __.Name = 
        protect <| fun () -> 
            sp.PUntaint((fun p -> p.Name), m)

    member __.DeclarationLocation = m

    member __.Kind = 
        protect <| fun () -> 
            let typ = Import.ImportProvidedType cenv.amap m (sp.PApply((fun x -> x.ParameterType), m))
            FSharpType(cenv,  typ)

    member __.IsOptional = 
        protect <| fun () -> sp.PUntaint((fun x -> x.IsOptional), m)

    member __.HasDefaultValue = 
        protect <| fun () -> sp.PUntaint((fun x -> x.HasDefaultValue), m)

    member __.DefaultValue = 
        protect <| fun () -> sp.PUntaint((fun x -> x.RawDefaultValue), m)

    override x.Equals(other : obj) =
        box x === other || 
        match other with
        |   :? FSharpStaticParameter as p -> x.Name = p.Name && x.DeclarationLocation = p.DeclarationLocation
        |   _ -> false

    override x.GetHashCode() = hash x.Name
    override x.ToString() = 
        "static parameter " + x.Name 

and FSharpParameter(cenv, typ:TType, topArgInfo:ArgReprInfo, mOpt, isParamArrayArg, isOutArg, isOptionalArg) = 
    inherit FSharpSymbol(cenv,  
                         (fun () -> 
                            let m = match mOpt with Some m  -> m | None -> range0
                            Item.ArgName((match topArgInfo.Name with None -> mkSynId m "" | Some v -> v), typ, None)),
                         (fun _ _ _ -> true))
    let attribs = topArgInfo.Attribs
    let idOpt = topArgInfo.Name
    let m = match mOpt with Some m  -> m | None -> range0
    member __.Name = match idOpt with None -> None | Some v -> Some v.idText
    member __.Type = FSharpType(cenv,  typ)
    member __.DeclarationLocation = match idOpt with None -> m | Some v -> v.idRange
    member __.Attributes = 
        attribs |> List.map (fun a -> FSharpAttribute(cenv,  AttribInfo.FSAttribInfo(cenv.g, a))) |> makeReadOnlyCollection
    member __.IsParamArrayArg = isParamArrayArg
    member __.IsOutArg = isOutArg
    member __.IsOptionalArg = isOptionalArg
    
    member private x.ValReprInfo = topArgInfo

    override x.Equals(other : obj) =
        box x === other || 
        match other with
        |   :? FSharpParameter as p -> x.Name = p.Name && x.DeclarationLocation = p.DeclarationLocation
        |   _ -> false

    override x.GetHashCode() = hash (box topArgInfo)
    override x.ToString() = 
        "parameter " + (match x.Name with None -> "<unnamed" | Some s -> s)

and FSharpAssemblySignature internal (cenv, mtyp: ModuleOrNamespaceType) = 

    new (g, thisCcu, tcImports, mtyp) = FSharpAssemblySignature(cenv(g,thisCcu,tcImports), mtyp)

    member __.Entities = 

        let rec loop (rmtyp : ModuleOrNamespaceType) = 
            [| for entity in rmtyp.AllEntities do
                   if entity.IsNamespace then 
                       yield! loop entity.ModuleOrNamespaceType
                   else 
                       yield FSharpEntity(cenv,  mkLocalEntityRef entity) |]
        
        loop mtyp |> makeReadOnlyCollection

    override x.ToString() = "<assembly signature>"

and FSharpAssembly internal (cenv, ccu: CcuThunk) = 

    new (g, thisCcu, tcImports, ccu) = FSharpAssembly(cenv(g,thisCcu,tcImports), ccu)

    member __.RawCcuThunk = ccu
    member __.QualifiedName = match ccu.QualifiedName with None -> "" | Some s -> s
    member __.CodeLocation = ccu.SourceCodeDirectory
    member __.FileName = ccu.FileName
    member __.SimpleName = ccu.AssemblyName 
    member __.IsProviderGenerated = ccu.IsProviderGenerated
    member __.Contents = FSharpAssemblySignature(cenv,  ccu.Contents.ModuleOrNamespaceType)
                 
    override x.ToString() = x.QualifiedName

type FSharpSymbol with 
    // TODO: there are several cases where we may need to report more interesting
    // symbol information below. By default we return a vanilla symbol.
    static member Create(g, thisCcu, tcImports,  item) : FSharpSymbol = 
        FSharpSymbol.Create (cenv(g,thisCcu,tcImports), item)

    static member Create(cenv,  item) : FSharpSymbol = 
        let dflt() = FSharpSymbol(cenv,  (fun () -> item), (fun _ _ _ -> true)) 
        match item with 
        | Item.Value v -> FSharpMemberOrFunctionOrValue(cenv,  V v, item) :> _
        | Item.UnionCase uinfo -> FSharpUnionCase(cenv,  uinfo.UnionCaseRef) :> _
        | Item.ExnCase tcref -> FSharpEntity(cenv,  tcref) :>_
        | Item.RecdField rfinfo -> FSharpField(cenv,  Recd rfinfo.RecdFieldRef) :> _
        
        | Item.Event einfo -> 
            FSharpMemberOrFunctionOrValue(cenv,  E einfo, item) :> _
            
        | Item.Property(_,pinfo :: _) -> 
            FSharpMemberOrFunctionOrValue(cenv,  P pinfo, item) :> _
            
        | Item.MethodGroup(_,minfo :: _) -> 
            FSharpMemberOrFunctionOrValue(cenv,  M minfo, item) :> _

        | Item.CtorGroup(_,cinfo :: _) -> 
            FSharpMemberOrFunctionOrValue(cenv,  M cinfo, item) :> _

        | Item.DelegateCtor (AbbrevOrAppTy tcref) -> 
            FSharpEntity(cenv,  tcref) :>_ 

        | Item.UnqualifiedType(tcref :: _)  
        | Item.Types(_,AbbrevOrAppTy tcref :: _) -> 
            FSharpEntity(cenv,  tcref) :>_  

        | Item.ModuleOrNamespaces(modref :: _) ->  
            FSharpEntity(cenv,  modref) :> _

        | Item.SetterArg (_id, item) -> FSharpSymbol.Create(cenv,  item)

        | Item.CustomOperation (_customOpName,_, Some minfo) -> 
            FSharpMemberOrFunctionOrValue(cenv,  M minfo, item) :> _

        | Item.CustomBuilder (_,vref) -> 
            FSharpMemberOrFunctionOrValue(cenv,  V vref, item) :> _

        | Item.TypeVar (_, tp) ->
             FSharpGenericParameter(cenv,  tp) :> _

        | Item.ActivePatternCase apref -> 
             FSharpActivePatternCase(cenv,  apref.ActivePatternInfo, apref.ActivePatternVal.Type, apref.CaseIndex, Some apref.ActivePatternVal, item) :> _

        | Item.ActivePatternResult (apinfo, typ, n, _) ->
             FSharpActivePatternCase(cenv,  apinfo, typ, n, None, item) :> _

        | Item.ArgName(id,ty,_)  ->
             FSharpParameter(cenv,  ty, {Attribs=[]; Name=Some id}, Some id.idRange, isParamArrayArg=false, isOutArg=false, isOptionalArg=false) :> _

        // TODO: the following don't currently return any interesting subtype
        | Item.ImplicitOp _
        | Item.ILField _ 
        | Item.FakeInterfaceCtor _
        | Item.NewDef _ -> dflt()
        // These cases cover unreachable cases
        | Item.CustomOperation (_, _, None) 
        | Item.UnqualifiedType []
        | Item.ModuleOrNamespaces []
        | Item.Property (_,[])
        | Item.MethodGroup (_,[])
        | Item.CtorGroup (_,[])
        // These cases cover misc. corned cases (non-symbol types)
        | Item.Types _
        | Item.DelegateCtor _  -> dflt()


