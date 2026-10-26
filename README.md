# PrivateChat · ChatFlow

Aplicación de chat con salas, mensajes privados, llamadas de voz y video, y pantalla compartida. El cliente activo es **ChatAppBlazor**, desarrollado con .NET MAUI Blazor Hybrid y MudBlazor. El servidor utiliza ASP.NET Core, MagicOnion/gRPC y MariaDB; las llamadas se integran con Janus mediante WebRTC.

## Funcionalidades

- Salas de chat y conversaciones privadas con adjuntos y vistas previas de enlaces.
- Edición y eliminación de mensajes, respuestas, reacciones, menciones e indicadores de escritura.
- Búsqueda de mensajes, contadores de no leídos y preferencias de notificación.
- Roles y moderación de salas y participantes.
- Llamadas de voz/video, pantalla compartida y anotaciones sobre la pantalla compartida.
- Distribución de actualizaciones para los clientes.
- Historial virtualizado, paginación por fecha e ID y recuperación de mensajes tras una reconexión.

## Estructura

| Proyecto o carpeta | Propósito |
| --- | --- |
| `ChatAppBlazor/` | Cliente principal MAUI Blazor Hybrid. |
| `ChatServer/` | Servidor ASP.NET Core: autenticación, chat, archivos, actualizaciones e integración con Janus. |
| `Shared/` | Contratos RPC, solicitudes, respuestas y modelos compartidos. |
| `ChatApp.Tests/` | Pruebas de paginación, recuperación del historial y consultas de no leídos. |
| `MusicBot/` | Cliente auxiliar para reproducción de música en llamadas. |
| `tools/` | Herramientas de compilación, publicación y despliegue. |
| `docs/` | Notas técnicas y documentación de implementación. |

## Requisitos

- SDK de .NET compatible con [`global.json`](global.json). Los proyectos activos apuntan a **.NET 11**.
- Workloads de MAUI correspondientes a la plataforma que se compile. `global.json` documenta también los requisitos de Android del SDK utilizado.
- MariaDB; el proveedor de EF Core está configurado para MariaDB 11.4.
- Janus con VideoRoom para las funciones de voz, video y pantalla compartida.
- Redis opcional. Si la cadena de conexión está vacía, el servidor usa caché en memoria.
- Para publicar mediante los scripts: PowerShell, `tar` y acceso SSH al servidor.

Windows y Android son los destinos contemplados por el script de publicación. El proyecto también declara destinos Apple, que requieren las herramientas correspondientes.

## Configurar el servidor

En una copia nueva del repositorio, crea la configuración local a partir de las plantillas:

```powershell
Copy-Item ChatServer/appsettings.example.json ChatServer/appsettings.json
Copy-Item ChatServer/appsettings.Development.example.json ChatServer/appsettings.Development.json
```

Edita `ChatServer/appsettings.json` y configura:

| Opción | Uso |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | Base de datos MariaDB y credenciales de acceso. |
| `ConnectionStrings:Redis` | Redis opcional; deja una cadena vacía para usar memoria. |
| `Jwt:Key` | Secreto aleatorio para firmar tokens; reemplaza el valor de ejemplo por al menos 32 bytes aleatorios. |
| `Janus:Url` | Dirección HTTP o WebSocket de Janus. |
| `Janus:IceServers` | Cadena JSON con los servidores STUN/TURN necesarios para los clientes. |
| `Server:BaseUrl` | URL del servidor accesible desde los clientes. |
| `AppUpdate` | Versión, notas y enlaces de descarga para distribuir actualizaciones. |
| `Kestrel:Endpoints` | Direcciones y protocolos de escucha. |

Las plantillas incluyen valores de ejemplo. La configuración real, las bases locales y los archivos de `ChatServer/Uploads/` están excluidos de Git. ASP.NET Core también admite variables de entorno, por ejemplo `ConnectionStrings__DefaultConnection` y `Jwt__Key`.

```powershell
dotnet restore ChatServer/ChatServer.csproj
dotnet run --project ChatServer/ChatServer.csproj
```

Con la plantilla, Kestrel escucha gRPC/HTTP2 en `http://localhost:5001` y HTTP en `http://localhost:5002`. Las migraciones se aplican al iniciar cuando `Database:MigrateOnStartup` está habilitado. La base de datos debe estar disponible y el usuario configurado debe tener permisos para aplicar esas migraciones.

## Compilar el cliente activo

Desde Windows, para compilar únicamente el destino Windows:

```powershell
dotnet workload restore ChatAppBlazor/ChatAppBlazor.csproj -p:TargetFrameworks=net11.0-windows10.0.19041.0
dotnet build ChatAppBlazor/ChatAppBlazor.csproj -f net11.0-windows10.0.19041.0 -p:TargetFrameworks=net11.0-windows10.0.19041.0
```

Para Android, con su SDK y workload instalados:

```powershell
dotnet build ChatAppBlazor/ChatAppBlazor.csproj -f net11.0-android -p:TargetFrameworks=net11.0-android
```

El cliente define la dirección de conexión en [`GrpcChannelProvider.ServerUrl`](ChatAppBlazor/Services/GrpcChannelProvider.cs). Cámbiala para apuntar a tu instalación. El cliente utiliza esa misma dirección para gRPC y las API HTTP, por lo que el punto de entrada debe exponer ambos servicios; en el despliegue habitual esto se resuelve con un proxy HTTPS.

## Pruebas

```powershell
dotnet test ChatApp.Tests/ChatApp.Tests.csproj
```

La suite cubre páginas con timestamps iguales, deduplicación, recuperación de varias páginas tras una desconexión, descarte de respuestas obsoletas, traducción SQL con el proveedor MariaDB y cálculo de no leídos.

Consulta [las notas del historial](docs/chat-history.md) para conocer el funcionamiento y los límites de la virtualización y la recuperación. La recuperación trae mensajes nuevos; no reproduce un registro de ediciones o eliminaciones de mensajes antiguos realizadas mientras el cliente estaba desconectado.

## Publicación y despliegue

Configura los destinos a partir del archivo de ejemplo:

```powershell
Copy-Item tools/publish-release.settings.example.json tools/publish-release.settings.json
```

Para generar los artefactos sin desplegar:

```powershell
./tools/publish-release.ps1 -SkipDeploy
```

Para compilar y desplegar usando los destinos configurados:

```powershell
./tools/publish-release.ps1
```

El script publica el servidor para `linux-arm64` y sin incluir el runtime de .NET. La arquitectura y el runtime instalados en el servidor deben coincidir con la publicación. Los archivos generados quedan bajo `artifacts/` y no se versionan.

En el modo predeterminado `Merge`, el despliegue conserva la configuración remota y actualiza únicamente la sección `AppUpdate`. Si `RestartCommand` tiene el valor `none`, el reinicio del servicio debe realizarse por separado.

Despliega el servidor antes de los clientes que utilizan los nuevos RPC de paginación. Los RPC anteriores se conservan para compatibilidad.
