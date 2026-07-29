# Shenora Project Brief

## Overview

**Shenora** is a reusable Windows desktop application framework extracted from an existing application built with:

- .NET
- WinForms
- Microsoft WebView2
- React
- TypeScript

The name **Shenora** is inspired by **神阙**, representing the external vessel or developing body that hosts an application's logic and intelligence.

Shenora should provide the reusable desktop “body” for applications, while domain-specific features remain outside the framework.

It should allow developers to build modern React-based Windows desktop applications without repeatedly implementing WebView2 hosting, frontend/backend communication, dependency injection, window management, local resource handling, application lifecycle, and native desktop integrations.

---

## Primary Goal

Extract the generic desktop infrastructure from the current application into reusable .NET libraries and a React client package.

The resulting framework should make it possible to create a new desktop application with an architecture similar to:

```text
MyApplication
├── MyApplication.Desktop
├── MyApplication.Modules
├── MyApplication.Domain
└── MyApplication.Web
```

using reusable Shenora packages:

```text
Shenora
├── Shenora.Core
├── Shenora.WinForms
├── Shenora.WebView2
├── Shenora.Ipc
├── Shenora.Modules
└── @shenora/react
```

Shenora must not contain application-specific concepts such as mods, game profiles, skins, videos, recipes, customers, or business-domain entities.

---

## Core Responsibilities

### 1. Desktop Application Host

Provide a reusable application host responsible for:

- Starting the WinForms application
- Configuring dependency injection
- Creating the main application window
- Initialising WebView2
- Loading the React frontend
- Coordinating application startup and shutdown
- Handling unhandled exceptions
- Supporting development and production modes
- Exposing lifecycle events to modules

Suggested abstractions:

```csharp
IShenoraApplication
IShenoraApplicationBuilder
IShenoraApplicationLifetime
IShenoraWindow
IShenoraWindowFactory
```

Example usage:

```csharp
ShenoraApplication
    .CreateBuilder(args)
    .UseWinForms()
    .UseWebView2()
    .UseFrontend(options =>
    {
        options.DevelopmentUrl = "http://localhost:5173";
        options.ProductionPath = "wwwroot";
    })
    .AddModule<SettingsModule>()
    .AddModule<FilesModule>()
    .Build()
    .Run();
```

---

### 2. WebView2 Hosting

Extract all generic WebView2 behaviour into a reusable component.

Responsibilities include:

- WebView2 environment creation
- Runtime availability validation
- Browser initialisation
- Development URL loading
- Packaged frontend loading
- Virtual host name mapping
- Navigation management
- New-window interception
- Download handling hooks
- Permission request hooks
- WebView reload and recovery
- Developer tools configuration
- Web message registration
- Browser process failure handling

Suggested types:

```csharp
ShenoraWebViewHost
WebViewHostOptions
IWebViewEnvironmentFactory
IWebViewNavigationService
IWebViewResourceResolver
```

The framework should support both:

```text
Development:
http://localhost:5173
```

and:

```text
Production:
https://app.shenora.local/
```

using WebView2 virtual-host folder mapping for packaged frontend files.

---

### 3. IPC Communication

Provide reliable typed communication between React and .NET using:

```javascript
window.chrome.webview.postMessage(...)
```

The IPC layer should support:

- Requests and responses
- Unique request IDs
- Asynchronous handlers
- Notifications and events
- Typed error responses
- Cancellation
- Timeouts
- Logging and tracing
- Reconnection after page refresh
- Handler discovery or registration
- Serialisation using `System.Text.Json`

Suggested request structure:

```json
{
  "id": "request-id",
  "type": "request",
  "route": "settings.get",
  "payload": {}
}
```

Response:

```json
{
  "id": "request-id",
  "type": "response",
  "success": true,
  "payload": {}
}
```

Error:

```json
{
  "id": "request-id",
  "type": "response",
  "success": false,
  "error": {
    "code": "settings_not_found",
    "message": "Settings could not be loaded."
  }
}
```

Suggested .NET abstractions:

```csharp
IIpcBridge
IIpcDispatcher
IIpcHandler
IIpcRequestHandler<TRequest, TResponse>
IIpcNotificationHandler<TNotification>
IIpcSerializer
```

Example handler:

```csharp
public sealed class GetSettingsHandler
    : IIpcRequestHandler<GetSettingsRequest, SettingsResponse>
{
    public Task<SettingsResponse> HandleAsync(
        GetSettingsRequest request,
        CancellationToken cancellationToken)
    {
        // Application logic
    }
}
```

---

### 4. React Client Library

Create a small reusable TypeScript package for communicating with the Shenora host.

Suggested package:

```text
@shenora/react
```

It should provide:

```typescript
shenora.invoke<TResponse>(route, payload)
shenora.send(route, payload)
shenora.subscribe(eventName, callback)
shenora.unsubscribe(eventName, callback)
shenora.isAvailable()
```

React integrations should include:

```typescript
useShenora()
useShenoraEvent()
useShenoraQuery()
```

The client must register its WebView2 message listener early enough to survive React remounts and frontend routing changes.

Pending requests must be stored outside individual React components so that component remounting does not lose responses.

A page refresh should reset the frontend bridge safely and allow new requests to work immediately.

---

### 5. Module System

Shenora should support independently registered application modules.

A module may register:

- Dependency injection services
- IPC handlers
- Application startup tasks
- Application shutdown tasks
- Window behaviours
- Native services
- Frontend metadata
- Configuration sections

Suggested interface:

```csharp
public interface IShenoraModule
{
    void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration);

    void ConfigureApplication(
        IShenoraApplicationBuilder application);
}
```

Example:

```csharp
public sealed class SettingsModule : IShenoraModule
{
    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddIpcHandler<GetSettingsHandler>();
        services.AddIpcHandler<UpdateSettingsHandler>();
    }

    public void ConfigureApplication(
        IShenoraApplicationBuilder application)
    {
    }
}
```

Modules should not require direct references to the main WinForms form.

---

### 6. Window Management

Provide reusable native window abstractions.

Initial support should include:

- Main window creation
- Window title and icon
- Initial size and minimum size
- Maximisation and minimisation
- Full-screen mode
- Window position persistence
- Multiple-window support
- Closing interception
- Native window events
- Frontend-triggered window commands

Potential IPC routes:

```text
window.minimise
window.maximise
window.restore
window.close
window.setTitle
window.setFullscreen
```

Suggested abstractions:

```csharp
IShenoraWindow
IShenoraWindowManager
IShenoraWindowStateStore
```

---

### 7. Native Desktop Services

Provide optional abstractions for common native operations:

- File picker
- Folder picker
- Save-file dialog
- Clipboard
- Notifications
- Opening files
- Opening URLs
- Showing files in Explorer
- Drag and drop
- Application paths
- Operating-system information
- Single-instance application handling

These capabilities should be exposed through services and IPC handlers rather than being implemented directly inside the main form.

Suggested interfaces:

```csharp
IFileDialogService
IClipboardService
INotificationService
IShellService
IApplicationPathService
ISingleInstanceService
IDragDropService
```

---

## Project Boundaries

### Shenora should contain

- Desktop hosting infrastructure
- WinForms integration
- WebView2 integration
- IPC communication
- Application lifecycle
- Module registration
- Window abstraction
- Native desktop service abstractions
- React IPC client
- Generic configuration and diagnostics

### Shenora should not contain

- Domain entities
- Business workflows
- Product-specific database repositories
- Mod or profile management
- Video management logic
- AI or LLM orchestration
- Application-specific screens
- Application-specific IPC contracts
- Product branding and assets

The existing application should consume Shenora rather than being moved entirely into it.

---

## Suggested Solution Structure

```text
Shenora/
├── src/
│   ├── Shenora.Core/
│   │   ├── Application/
│   │   ├── Configuration/
│   │   ├── DependencyInjection/
│   │   ├── Lifecycle/
│   │   └── Modules/
│   │
│   ├── Shenora.Ipc/
│   │   ├── Contracts/
│   │   ├── Dispatching/
│   │   ├── Handlers/
│   │   ├── Serialization/
│   │   └── Errors/
│   │
│   ├── Shenora.WebView2/
│   │   ├── Hosting/
│   │   ├── Navigation/
│   │   ├── Resources/
│   │   └── Diagnostics/
│   │
│   ├── Shenora.WinForms/
│   │   ├── Application/
│   │   ├── Windows/
│   │   ├── Dialogs/
│   │   ├── DragDrop/
│   │   └── Shell/
│   │
│   └── Shenora.React/
│       ├── src/
│       │   ├── bridge/
│       │   ├── hooks/
│       │   ├── messages/
│       │   └── index.ts
│       └── package.json
│
├── samples/
│   ├── Shenora.Sample.Desktop/
│   └── Shenora.Sample.Web/
│
├── tests/
│   ├── Shenora.Core.Tests/
│   ├── Shenora.Ipc.Tests/
│   ├── Shenora.WebView2.Tests/
│   └── Shenora.IntegrationTests/
│
├── Directory.Build.props
├── Directory.Packages.props
└── Shenora.sln
```

---

## Extraction Roadmap

### Phase 1 — Audit the Existing Application

Identify code related to:

- Application startup
- Dependency injection
- Main form creation
- WebView2 initialisation
- React frontend loading
- Message sending and receiving
- Message dispatching
- Window operations
- Drag and drop
- Native dialogs
- File-system operations
- Development and production configuration

Classify each component as:

```text
Generic framework code
Application-specific code
Mixed code requiring separation
```

Do not move mixed components directly. First separate generic infrastructure from domain behaviour.

---

### Phase 2 — Extract the Core Host

Create:

```text
Shenora.Core
Shenora.WinForms
Shenora.WebView2
```

Move the minimum code required to:

1. Start a WinForms application.
2. Create a window.
3. Initialise WebView2.
4. Load a React frontend.
5. Shut down cleanly.

At the end of this phase, create a minimal sample application that displays a React page inside WebView2.

---

### Phase 3 — Extract IPC

Replace application-specific message handling with a route-based dispatcher.

Implement:

- Request envelope
- Response envelope
- Notification envelope
- Dispatcher
- Handler registration
- Error conversion
- Cancellation support
- TypeScript client

At the end of this phase, the sample application should invoke a .NET handler from React and receive a typed response.

---

### Phase 4 — Extract Modules and Native Services

Add:

- Module registration
- Lifecycle hooks
- Window manager
- File and folder dialogs
- Clipboard access
- Shell operations
- Drag-and-drop abstraction
- Single-instance support

Keep each feature optional and registered through dependency injection.

---

### Phase 5 — Migrate the Existing Application

Update the current desktop application to consume Shenora packages.

Replace:

- Custom WebView initialisation
- Custom message dispatcher
- Direct form dependencies
- Global static IPC state
- Application-specific bridge setup

with Shenora abstractions.

Application-specific handlers and services should remain inside the current application.

Migration should be incremental. The existing application must remain runnable after each extraction step.

---

### Phase 6 — Stabilisation and Packaging

Add:

- Unit tests
- Integration tests
- Structured logging
- API documentation
- Sample applications
- NuGet package metadata
- npm package metadata
- Semantic versioning
- GitHub Actions build and release workflow

Initial package names:

```text
Shenora.Core
Shenora.Ipc
Shenora.WebView2
Shenora.WinForms
Shenora.Extensions.DependencyInjection
@shenora/react
```

---

## Important Technical Requirements

### Dependency Injection

Use standard Microsoft abstractions:

```text
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Configuration
Microsoft.Extensions.Logging
Microsoft.Extensions.Options
```

Avoid creating a custom dependency injection container.

### Serialisation

Use:

```text
System.Text.Json
```

Provide configurable `JsonSerializerOptions`.

### Threading

WinForms UI operations must execute on the UI thread.

Provide a reusable dispatcher:

```csharp
IUiDispatcher.InvokeAsync(...)
```

IPC handlers should not block the UI thread unless they are performing UI operations.

### Error Handling

Do not expose raw exception stack traces to the frontend by default.

Convert exceptions into structured IPC errors and log the original exception on the .NET side.

### Logging

Use `ILogger<T>` throughout the framework.

Important operations to log:

- Application startup
- WebView2 initialisation
- Navigation failures
- IPC requests and failures
- Handler execution duration
- Browser process failures
- Application shutdown

### Compatibility

Initial target:

```text
.NET 8
Windows 10 and later
WebView2 Evergreen Runtime
React 18 or later
TypeScript 5 or later
```

Avoid coupling the framework to a specific React build tool. Vite may be used in the sample project, but Shenora should support any frontend that produces static files.

---

## Initial Success Criteria

The first usable release is complete when a developer can:

1. Create a .NET WinForms project.
2. Register Shenora.
3. Load a React application in WebView2.
4. Call a typed .NET handler from React.
5. Receive native events in React.
6. Open native file and folder dialogs.
7. Control the desktop window from React.
8. Add application functionality through modules.
9. Run with a Vite development server.
10. Package and run with embedded static frontend files.

---

## Instructions for the Coding Agent

Start by inspecting the current project and preparing an extraction map.

Do not immediately rewrite the architecture.

For every existing class, determine:

```text
1. Is this generic enough for Shenora?
2. Does it depend on application-domain types?
3. Can those dependencies be replaced with interfaces?
4. Should the class move, be split, or remain?
```

Prefer extracting working code over replacing it with speculative abstractions.

Maintain existing behaviour while introducing clear framework boundaries.

Implement the extraction in small, buildable commits:

```text
1. Add project structure.
2. Extract one responsibility.
3. Update the existing application to consume it.
4. Build and test.
5. Continue to the next responsibility.
```

The first implementation target should be:

```text
Shenora.Core
Shenora.WinForms
Shenora.WebView2
Shenora.Ipc
@shenora/react
```

Advanced features such as plugin marketplaces, automatic frontend contract generation, cross-platform support, and alternative webview engines are outside the initial scope.

---

## Relationship to Lyntai

The two projects should remain separate:

```text
Lyntai
└── Reusable AI brain, memory, persistence, LLM routing, evaluation, and tracing

Shenora
└── Reusable Windows desktop host, WebView2 shell, IPC, modules, and native integration
```

Shenora may host applications that use Lyntai, but Shenora must not depend on Lyntai directly.
