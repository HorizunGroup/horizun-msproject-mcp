# Benchmark — MCP servers para Microsoft Project

Fecha del relevamiento: 2026-08-03. Todos los datos de estrellas / fechas vienen de la GitHub API el mismo día.

## Resumen del mercado

El mercado es **muy inmaduro**: todos los servidores encontrados nacieron en 2026 y ninguno pasa de 7 estrellas.
No existe un MCP oficial de Microsoft para Project (la apuesta oficial de Microsoft es Planner Premium /
Dataverse, que es otro producto). Hay espacio real para un servidor serio.

## Los 4 arquetipos técnicos

| # | Arquetipo | Cómo lee/escribe | Plataforma | Escribe .mpp nativo | Fidelidad de cálculo |
|---|-----------|------------------|-----------|--------------------|---------------------|
| A | **MPXJ** (Java + JPype/Python) | Parseo directo del binario | Cross-platform, sin MS Project | ❌ No (solo MSPDI .xml) | Motor propio, no el de MS Project |
| B | **COM automation** (pywin32 / C# interop) | Pilota MS Project | Solo Windows + licencia MS Project | ✅ Sí | ✅ Total (motor real) |
| C | **JDBC/SQL** (CData) | Driver comercial sobre Project Online | Cross-platform, driver de pago | ❌ Read-only | N/A |
| D | **Graph API** (Planner) | REST | Cloud | N/A — otro producto | N/A |

## Tabla comparativa

| Servidor | Arq. | Lenguaje | Tools | R/W | Formatos | Licencia | ⭐ | Último push |
|---|---|---|---|---|---|---|---|---|
| [elsahafy/ms-procject-mcp](https://github.com/elsahafy/ms-procject-mcp) | B (COM) | Python 3.10+ / FastMCP | **79** | R+W completo | .mpp nativo | MIT | 7 | 2026-03-14 |
| [jeffmodeler/project-mcp](https://github.com/jeffmodeler/project-mcp) (*lean-planning-mcp*) | A (MPXJ opc.) | Python 3.11+ | **49** (14 core + 17 AWP + 18 LPS) | Read-only + sidecar JSON | .xml, .mpp, .mpx, .xer, .pmxml, .sp, .pp | MIT | 1 | 2026-07-13 |
| [albertito1998/mcp_ms_project](https://github.com/albertito1998/mcp_ms_project) | A (MPXJ) | Python 3.10+ / Java 11+ | 14 | R+W (escribe a .xml) | in: .mpp/.mpt/.mpx/.xml/.xer/.pp — out: .xml/.mpx/.json/.xer | MIT | 0 | 2026-03-28 |
| [HurleySk/mpp-mcp](https://github.com/HurleySk/mpp-mcp) | B (COM) | C# / .NET | ~15 | R+W | .mpp nativo | MIT | 0 | 2026-04-20 |
| [CDataSoftware/microsoft-project-mcp-server-by-cdata](https://github.com/CDataSoftware/microsoft-project-mcp-server-by-cdata) | C | Java / Maven | 3 (get_tables, get_columns, run_query) | Read-only | Project Online vía JDBC | MIT (driver de pago) | — | — |
| [cygnusyang/omniplan-mcp](https://github.com/cygnusyang/omniplan-mcp) | AppleScript | Python | ~12 | R+W (requiere app abierta) | .oplx nativo, .mpp vía puente | MIT | 0 | ~2026-07 |
| [softeria/ms-365-mcp-server](https://github.com/softeria/ms-365-mcp-server) | D | TypeScript | muchos | R+W | Planner (NO Project) | MIT | alto | activo |

## Análisis por servidor

### 1. elsahafy/ms-procject-mcp — el más completo funcionalmente
79 tools vía COM: tareas, dependencias, recursos, calendarios, líneas base, earned value,
nivelación, what-if, timephased, multiproyecto, bulk ops, dry-run, undo.
- ✅ Único con **escritura nativa .mpp** y motor de cálculo real de MS Project.
- ✅ 202 tests.
- ❌ 79 tools = ~15–25k tokens de esquema en cada turno. Ruido brutal de contexto.
- ❌ Un solo archivo de ~5.200 líneas. Difícil de mantener.
- ❌ Problemas declarados: proxy COM se pone stale al cambiar de proyecto, undo limitado a 10,
  bloqueo de archivo entre GUI y servidor, timephased lento.
- ❌ Sin verificación post-commit: si Project rechaza un write (tarea manual, constraint, campo
  calculado), el tool puede reportar éxito igual.

### 2. jeffmodeler/project-mcp (lean-planning-mcp) — el más “de dominio”
Es el más interesante conceptualmente: sobre 14 tools de lectura monta **AWP** (Advanced Work
Packaging: CWA/CWP/EWP/PWP/IWP, readiness gates, path of construction) y **LPS** (Last Planner:
pull plan, restricciones, lookahead, compromisos, PPC/TA/TMR).
- ✅ Read-only por diseño: “los edits se quedan en Microsoft Project”. Postura defendible.
- ✅ MSPDI .xml sin JVM; MPXJ opcional para el resto.
- ✅ Multiformato real (P6, Synchro, Asta) — sirve para construcción.
- ✅ `generate_pbip_dashboard` → sale a Power BI.
- ❌ No escribe cronograma. La metodología vive en JSON sidecar, no en el .mpp.

### 3. albertito1998/mcp_ms_project — el mínimo viable MPXJ
14 tools, CRUD básico. Escribe pero a MSPDI .xml, o sea el usuario tiene que reabrir y
reguardar en Project. Repo de 1 día (creado y pusheado el 2026-03-28). Referencia útil, no producto.

### 4. HurleySk/mpp-mcp — COM en C#
Mismo arquetipo que elsahafy pero en .NET y con ~15 tools. Interesante como referencia si se
quiere empaquetar como add-in/EXE en vez de Python. Cero tracción.

### 5. CData — la vía enterprise
3 tools genéricos de SQL sobre el driver JDBC. Es un patrón "text-to-SQL", no un MCP de dominio:
el LLM tiene que descubrir el esquema y armar el SELECT. Read-only y el CRUD real está detrás de
CData Connect AI (de pago). Solo tiene sentido si el cliente ya está en Project Online/Server.

### 6. omniplan-mcp / ms-365-mcp-server — fuera de target
El primero es macOS/OmniPlan. El segundo es Planner vía Graph, no cronogramas de Project.

## Huecos del mercado (= dónde diferenciarse)

1. **Nadie escribe .mpp nativo de forma cross-platform.** COM ata a Windows; MPXJ no escribe .mpp.
   El punto dulce: COM en Windows + fallback MPXJ→MSPDI headless en servidor/CI.
2. **Nadie verifica lo que escribe.** MS Project recalcula, mueve fechas y silenciosamente ignora
   writes (task mode manual/auto, constraints, campos calculados). El patrón "escribir → releer →
   fallar si no coincide" no existe en ninguno.
3. **Explosión de tools.** 79 tools mata el contexto. Nadie agrupa por verbo + payload tipado.
4. **Nadie muestra el radio de impacto antes de escribir.** Cambiar una duración puede mover 400
   tareas aguas abajo. Falta dry-run con diff de cronograma (fechas antes/después, ruta crítica antes/después).
5. **Cero integración BIM.** Ningún servidor conecta Project con Revit / Navisworks / Power BI.
   Un cronograma 4D/5D (tarea ↔ elemento del modelo ↔ código de presupuesto) no existe en el mercado.
6. **Nadie hace QA de cronograma** al estilo DCMA-14 (lags negativos, constraints duros, tareas sin
   sucesor, float excesivo, duraciones >44d). Los MCP de P6 sí lo hacen; los de Project no.

## Recomendación de arquitectura para nuestro MCP

- **Backend dual**: adaptador COM (Windows, escritura nativa, motor real) + adaptador MPXJ
  (headless, lectura multiformato, escritura MSPDI). Misma superficie de tools.
- **~20–25 tools consolidados**, no 79. Agrupar: `project_open/save/info`, `tasks_query`,
  `tasks_write` (batch tipado), `deps_write`, `resources_*`, `schedule_analyze`, `baseline_*`,
  `qa_check`, `export_*`.
- **Contrato de escritura verificada**: todo write releé del modelo tras el commit y falla si no
  coincide; el conteo sale de la relectura, no de "no lanzó excepción". Es el mismo contrato del
  MCP de Revit que ya usamos.
- **dry_run obligatorio en writes masivos**, con diff: cuántas tareas se mueven, cuánto se corre
  la fecha fin, si cambia la ruta crítica.
- **QA de cronograma tipo DCMA-14** como tool de primera clase.
- **Puente BIM**: mapear tareas ↔ elementos de Revit por código (p.ej. `HRZ_COD_PRES`) y exportar
  el dataset que consume Power BI. Ese es el diferenciador que nadie tiene.

## Fuentes

- https://github.com/jeffmodeler/project-mcp
- https://github.com/elsahafy/ms-procject-mcp
- https://github.com/albertito1998/mcp_ms_project
- https://github.com/HurleySk/mpp-mcp
- https://github.com/CDataSoftware/microsoft-project-mcp-server-by-cdata
- https://github.com/cygnusyang/omniplan-mcp
- https://github.com/softeria/ms-365-mcp-server
- https://github.com/joniles/mpxj — https://www.mpxj.org/
- https://learn.microsoft.com/en-us/power-platform/release-plan/2025wave1/data-platform/dataverse-mcp-server
