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

open System.Collections.Generic
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Env
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Build


/// Represents the definitional contents of an assembly, as seen by the F# language
type [<Class>] FSharpAssemblyContents = 

    internal new : tcGlobals: TcGlobals * thisCcu: CcuThunk * tcImports: TcImports * mimpls: TypedImplFile list -> FSharpAssemblyContents

    /// The contents of the implementation files in the assembly
    member ImplementationFiles:  FSharpImplementationFileContents list

/// Represents the definitional contents of a single file or fragment in an assembly, as seen by the F# language
and [<Class>] FSharpImplementationFileContents = 

    /// Get the declarations that make up this implementation file
    member Declarations : FSharpImplementationFileDeclaration list

    /// Indicates if the implementation file is a script
    member IsScript:  bool

    /// Indicates if the implementation file has an explicit entry point
    member HasExplicitEntryPoint:  bool

/// Represents a declaration in an implementation file, as seen by the F# language
and FSharpImplementationFileDeclaration = 
    /// Represents the declaration of a type
    | Entity of FSharpEntity * FSharpImplementationFileDeclaration list
    /// Represents the declaration of a member, function or value, including the parameters and body of the member
    | MemberOrFunctionOrValue  of FSharpMemberOrFunctionOrValue * FSharpMemberOrFunctionOrValue list list * FSharpExpr
    /// Represents the declaration of a static initialization action
    | InitAction of FSharpExpr

/// Represents a checked and reduced expression, as seen by the F# language.  The active patterns 
/// in 'FSharp.Compiler.SourceCodeServices' can be used to analyze information about the expression.
///
/// Pattern matching is reduced to decision trees and conditional tests. Some other
/// constructs may be represented in reduced form.
and [<Sealed>]  FSharpExpr =
    /// The range of the expression
    member Range : range
    /// The type of the expression
    member Type : FSharpType

/// Represents a checked method in an object expression, as seen by the F# language.  
and [<Sealed>]  FSharpObjectExprOverride = 
    /// The overridden member of the base class
    member OverriddenMember : FSharpMemberFunctionOrValue 
    /// The generic parameters of the method
    member GenericParameters : FSharpGenericParameter list
    /// The parameters of the method
    member CurriedParameterGroups : FSharpMemberFunctionOrValue list list
    /// The expression that forms the body of the method
    member Body : FSharpExpr

/// A collection of active patterns to analyze expressions
module BasicPatterns =

    /// Matches expressions which are uses of values 
    val (|Value|_|) : FSharpExpr -> FSharpMemberOrFunctionOrValue option 

    /// Matches expressions which are the application of function values 
    val (|Application|_|) : FSharpExpr -> (FSharpExpr * FSharpType list * FSharpExpr list) option 

    /// Matches expressions which are type abstractions
    val (|TypeLambda|_|) : FSharpExpr -> (FSharpGenericParameter list * FSharpExpr) option   

    /// Matches expressions with a decision expression, each branch of which ends in DecisionTreeSuccess pasing control and values to one of the targets.
    val (|DecisionTree|_|) : FSharpExpr -> (FSharpExpr * (FSharpMemberOrFunctionOrValue list * FSharpExpr) list) option

    /// Special expressions at the end of a conditional decision structure in the decision expression node of a DecisionTree .
    /// The given expressions are passed as values to the decision tree target.
    val (|DecisionTreeSuccess|_|) : FSharpExpr -> (int * FSharpExpr list) option

    /// Matches expressions which are lambda abstractions
    val (|Lambda|_|) : FSharpExpr -> (FSharpMemberOrFunctionOrValue * FSharpExpr) option   

    /// Matches expressions which are conditionals
    val (|IfThenElse|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr * FSharpExpr) option   

    /// Matches expressions which are let definitions
    val (|Let|_|) : FSharpExpr -> ((FSharpMemberOrFunctionOrValue * FSharpExpr) * FSharpExpr) option 

    /// Matches expressions which are calls to members or module-defined functions. When calling curried functions and members the
    /// arguments are collapsed to a single collection of arguments, as done in the compiled version of these.
    val (|Call|_|) : FSharpExpr -> (FSharpExpr option * FSharpMemberFunctionOrValue * FSharpType list * FSharpType list * FSharpExpr list) option 

    /// Matches expressions which are calls to object constructors 
    val (|NewObject|_|) : FSharpExpr -> (FSharpMemberFunctionOrValue * FSharpType list * FSharpExpr list) option 

    /// Matches expressions which are uses of the 'this' value
    val (|ThisValue|_|) : FSharpExpr -> FSharpType  option

    /// Matches expressions which are uses of the 'base' value
    val (|BaseValue|_|) : FSharpExpr -> FSharpType  option

    /// Matches expressions which are quotation literals
    val (|Quote|_|) : FSharpExpr -> FSharpExpr  option

    /// Matches expressions which are let-rec definitions
    val (|LetRec|_|) : FSharpExpr -> ((FSharpMemberOrFunctionOrValue * FSharpExpr) list * FSharpExpr) option 

    /// Matches record expressions 
    val (|NewRecord|_|) : FSharpExpr -> (FSharpType * FSharpExpr list) option 

    /// Matches expressions which get a field from a record or class
    val (|FSharpFieldGet|_|) : FSharpExpr -> (FSharpExpr option * FSharpType * FSharpField) option 

    /// Matches expressions which set a field in a record or class
    val (|FSharpFieldSet|_|) : FSharpExpr -> (FSharpExpr option * FSharpType * FSharpField * FSharpExpr) option 

    /// Matches expressions which create an object corresponding to a union case
    val (|NewUnionCase|_|) : FSharpExpr -> (FSharpType * FSharpUnionCase * FSharpExpr list) option 

    /// Matches expressions which get a field from a union case
    val (|UnionCaseGet|_|) : FSharpExpr -> (FSharpExpr * FSharpType  * FSharpUnionCase * FSharpField) option 

    /// Matches expressions which set a field from a union case (only used in FSharp.Core itself)
    val (|UnionCaseSet|_|) : FSharpExpr -> (FSharpExpr * FSharpType  * FSharpUnionCase * FSharpField * FSharpExpr) option 

    /// Matches expressions which gets the tag for a union case
    val (|UnionCaseTag|_|) : FSharpExpr -> (FSharpExpr * FSharpType) option 

    /// Matches expressions which test if an expression corresponds to a particular union case
    val (|UnionCaseTest|_|) : FSharpExpr -> (FSharpExpr * FSharpType * FSharpUnionCase) option 

    /// Matches tuple expressions 
    val (|NewTuple|_|) : FSharpExpr -> (FSharpType * FSharpExpr list) option 

    /// Matches expressions which get a value from a tuple
    val (|TupleGet|_|) : FSharpExpr -> (FSharpType * int * FSharpExpr) option 

    /// Matches expressions which coerce the type of a value
    val (|Coerce|_|) : FSharpExpr -> (FSharpType * FSharpExpr) option 

    /// Matches array expressions 
    val (|NewArray|_|) : FSharpExpr -> (FSharpType * FSharpExpr list) option 

    /// Matches expressions which test the runtime type of a value
    val (|TypeTest|_|) : FSharpExpr -> (FSharpType * FSharpExpr) option 

    /// Matches expressions which set the contents of an address
    val (|AddressSet|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr) option 

    /// Matches expressions which set the contents of a mutable variable
    val (|ValueSet|_|) : FSharpExpr -> (FSharpMemberOrFunctionOrValue * FSharpExpr) option 

    /// Matches default-value expressions, including null expressions
    val (|DefaultValue|_|) : FSharpExpr -> FSharpType option 

    /// Matches constant expressions, including signed and unsigned integers, strings, characters, booleans, arrays
    /// of bytes and arrays of unit16.
    val (|Const|_|) : FSharpExpr -> (obj * FSharpType) option 

    /// Matches expressions which take the address of a location
    val (|AddressOf|_|) : FSharpExpr -> FSharpExpr option

    /// Matches sequential expressions 
    val (|Sequential|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr) option 

    /// Matches fast-integer loops (up or down)
    val (|FastIntegerForLoop|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr * FSharpExpr * bool) option  

    /// Matches while loops 
    val (|WhileLoop|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr) option 

    /// Matches try/finally expressions
    val (|TryFinally|_|) : FSharpExpr -> (FSharpExpr * FSharpExpr) option 

    /// Matches try/with expressions
    val (|TryWith|_|) : FSharpExpr -> (FSharpExpr * FSharpMemberOrFunctionOrValue * FSharpExpr * FSharpMemberOrFunctionOrValue * FSharpExpr) option 

    /// Matches expressions which create an instance of a delegate type
    val (|NewDelegate|_|) : FSharpExpr -> (FSharpType * FSharpExpr) option 

    /// Matches expressions which are IL assembly code
    val (|ILAsm|_|) : FSharpExpr -> (string * FSharpType list * FSharpExpr list) option 

    /// Matches expressions which fetch a field from a .NET type
    val (|ILFieldGet|_|) : FSharpExpr -> (FSharpExpr option * FSharpType * string) option 

    /// Matches expressions which set a field in a .NET type
    val (|ILFieldSet|_|) : FSharpExpr -> (FSharpExpr option * FSharpType * string * FSharpExpr) option 

    /// Matches object expressions, returning the base type, the base call, the overrides and the interface implementations
    val (|ObjectExpr|_|) : FSharpExpr -> (FSharpType * FSharpExpr * FSharpObjectExprOverride list * (FSharpType * FSharpObjectExprOverride list) list) option

    /// Matches expressions for an unresolved call to a trait 
    val (|TraitCall|_|) : FSharpExpr -> (FSharpType list * string * FSharpType list * FSharpType list * FSharpExpr list) option 


