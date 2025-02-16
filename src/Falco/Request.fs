[<RequireQualifiedAccess>]
module Falco.Request

open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Falco.Security
open Falco.StringUtils

/// Obtains the HttpVerb of the request
let getVerb (ctx : HttpContext) : HttpVerb =
    match ctx.Request.Method with
    | m when strEquals m HttpMethods.Get     -> GET
    | m when strEquals m HttpMethods.Head    -> HEAD
    | m when strEquals m HttpMethods.Post    -> POST
    | m when strEquals m HttpMethods.Put     -> PUT
    | m when strEquals m HttpMethods.Patch   -> PATCH
    | m when strEquals m HttpMethods.Delete  -> DELETE
    | m when strEquals m HttpMethods.Options -> OPTIONS
    | m when strEquals m HttpMethods.Trace   -> TRACE
    | _ -> ANY

/// Streams the request body into a string.
let getBodyString (ctx : HttpContext) : Task<string> =
    task {
        use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
        return! reader.ReadToEndAsync()
    }

/// Retrieves the cookie from the request as an instance of
/// CookieCollectionReader.
let getCookie (ctx : HttpContext) : CookieCollectionReader =
    CookieCollectionReader(ctx.Request.Cookies)

/// Retrieves a specific header from the request.
let getHeaders (ctx : HttpContext) : HeaderCollectionReader  =
    HeaderCollectionReader(ctx.Request.Headers)

/// Retrieves all route values from the request as RouteCollectionReader.
let getRoute (ctx : HttpContext) : RouteCollectionReader =
    RouteCollectionReader(ctx.Request.RouteValues, ctx.Request.Query)

/// Retrieves the query string from the request as an instance of
/// QueryCollectionReader.
let getQuery (ctx : HttpContext) : QueryCollectionReader =
    QueryCollectionReader(ctx.Request.Query)

/// Retrieves the form collection from the request as an instance of
/// FormCollectionReader.
let getForm (ctx : HttpContext) : Task<FormCollectionReader> =
    task {
        let! form = ctx.Request.ReadFormAsync()
        let files = if isNull(form.Files) then None else Some form.Files
        return FormCollectionReader(form, files)
    }

/// Retrieves the form collection from the request as an instance of
/// FormCollectionReader, if the CSRF token is valid, otherwise it
/// returns None.
let getFormSecure (ctx : HttpContext) : Task<FormCollectionReader option> =
    task {
        let! isAuth = Xss.validateToken ctx
        if isAuth then
            let! form = getForm ctx
            return Some form
        else
            return None

    }

/// Streams the form collection from the request as an instance of
/// FormCollectionReader. Intended to be used for multipart form submissions.
let streamForm
    (ctx : HttpContext) : Task<FormCollectionReader> =
    task {
        let! form = ctx.Request.StreamFormAsync()
        let files = if isNull(form.Files) then None else Some form.Files
        return FormCollectionReader(form, files)
    }

/// Streams the form collection from the request as an instance of
/// FormCollectionReader, if the CSRF token is valid. otherwise it returns
/// None. Intended to be used for multipart form submissions.
let streamFormSecure (ctx : HttpContext) : Task<FormCollectionReader option> =
    task {
        let! isAuth = Xss.validateToken ctx
        if isAuth then
            let! form = streamForm ctx
            return Some form
        else
            return None

    }

/// Attempts to bind request body using System.Text.Json and provided
/// JsonSerializerOptions.
let getJsonOptions<'T>
    (options : JsonSerializerOptions)
    (ctx : HttpContext) : Task<'T> =
    JsonSerializer.DeserializeAsync<'T>(ctx.Request.Body, options).AsTask()

// ------------
// Handlers
// ------------

/// Buffers the current HttpRequest body into a
/// string and provides to next HttpHandler.
let bodyString
    (next : string -> HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! body = getBodyString ctx
        return! next body ctx
    }

/// Projects CookieCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapCookie
    (map : CookieCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    getCookie ctx
    |> map
    |> fun cookie -> next cookie ctx

/// Projects HeaderCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapHeader
    (map : HeaderCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
        getHeaders ctx
        |> map
        |> fun header -> next header ctx

/// Projects RouteCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapRoute
    (map : RouteCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    getRoute ctx
    |> map
    |> fun route -> next route ctx

/// Projects QueryCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapQuery
    (map : QueryCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    getQuery ctx
    |> map
    |> fun query -> next query ctx


/// Projects FormCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapForm
    (map : FormCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! form = getForm ctx
        return! next (map form) ctx
    }

/// Streams multipart/form-data into FormCollectionReader and projects onto 'T
/// and provides to next HttpHandler.
///
/// Important: This is intended to be used with multipart/form-data submissions
/// and will not work if this content-type is not present.
let mapFormStream
    (map : FormCollectionReader -> 'T)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! form = streamForm ctx
        return! next (map form) ctx
    }

/// Validates the CSRF of the current request.
let validateCsrfToken
    (handleOk : HttpHandler)
    (handleInvalidToken : HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! isValid = Xss.validateToken ctx

        let respondWith =
            match isValid with
            | true  -> handleOk
            | false -> handleInvalidToken

        return! respondWith ctx
    }

/// Projects FormCollectionReader onto 'T and provides
/// to next HttpHandler.
let mapFormSecure
    (map : FormCollectionReader -> 'T)
    (next : 'T -> HttpHandler)
    (handleInvalidToken : HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! form = getFormSecure ctx

        let respondWith =
            match form with
            | Some form ->
                next (map form)
            | None ->
                handleInvalidToken

        return! respondWith ctx
    }

/// Streams multipart/form-data into FormCollectionReader and projects onto 'T
/// and provides to next HttpHandler.
///
/// Important: This is intended to be used with multipart/form-data submissions
/// and will not work if this content-type is not present.
let mapFormStreamSecure
    (map : FormCollectionReader -> 'T)
    (next : 'T -> HttpHandler)
    (handleInvalidToken : HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! form = streamFormSecure ctx

        let respondWith =
            match form with
            | Some form ->
                next (map form)
            | None ->
                handleInvalidToken

        return! respondWith ctx
    }

/// Projects JSON using custom JsonSerializerOptions
/// onto 'T and provides to next HttpHandler, throws
/// JsonException if errors occur during deserialization.
let mapJsonOption
    (options : JsonSerializerOptions)
    (next : 'T -> HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! json = getJsonOptions options ctx
        return! next json ctx
    }

let internal defaultJsonOptions =
    let options = JsonSerializerOptions()
    options.AllowTrailingCommas <- true
    options.PropertyNameCaseInsensitive <- true
    options

/// Projects JSON onto 'T and provides to next
/// HttpHandler, throws JsonException if errors
/// occur during deserialization.
let mapJson
    (next : 'T -> HttpHandler) : HttpHandler =
    mapJsonOption defaultJsonOptions next

// ------------
// Authentication
// ------------

/// Attempts to authenticate the current request using the provided
/// scheme and passes AuthenticateResult into next HttpHandler.
let authenticate
    (scheme : string)
    (next : AuthenticateResult -> HttpHandler) : HttpHandler = fun ctx ->
    task {
        let! authenticateResult = Auth.authenticate scheme ctx
        return! next authenticateResult ctx
    }

/// Proceeds if the authentication status of current IPrincipal is true.
let ifAuthenticated
    (handleOk : HttpHandler)
    (handleError : HttpHandler) : HttpHandler = fun ctx ->
    let isAuthenticated = Auth.isAuthenticated ctx
    if isAuthenticated then handleOk ctx
    else handleError ctx

/// Proceeds if the authentication status of current IPrincipal is true
/// and they exist in a list of roles.
let ifAuthenticatedInRole
    (roles : string list)
    (handleOk : HttpHandler)
    (handleError : HttpHandler) : HttpHandler =
    fun ctx ->
        let isAuthenticated = Auth.isAuthenticated ctx
        let isInRole = Auth.isInRole roles ctx

        match isAuthenticated, isInRole with
        | true, true -> handleOk ctx
        | _          -> handleError ctx

/// Proceeds if the authentication status of current IPrincipal is true
/// and has a specific scope.
let ifAuthenticatedWithScope
    (issuer : string)
    (scope : string)
    (handleOk : HttpHandler)
    (handleError : HttpHandler) : HttpHandler =
    fun ctx ->
        let isAuthenticated = Auth.isAuthenticated ctx
        let hasScope = Auth.hasScope issuer scope ctx

        match isAuthenticated, hasScope with
        | true, true -> handleOk ctx
        | _          -> handleError ctx

/// Proceeds if the authentication status of current IPrincipal is false.
let ifNotAuthenticated
    (handleOk : HttpHandler)
    (handleError : HttpHandler) : HttpHandler = fun ctx ->
    let isAuthenticated = Auth.isAuthenticated ctx
    if isAuthenticated then handleError ctx
    else handleOk ctx
