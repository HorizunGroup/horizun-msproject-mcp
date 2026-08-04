# Horizun Project MCP — Diseño de la superficie de tools

Objetivo: **20 tools** que cubran más terreno útil que los 79 de la competencia, con un contrato de
escritura que ninguno tiene.

---

## 0. Los 6 principios de diseño

Estos principios mandan sobre cualquier decisión puntual. Si un tool los viola, el tool está mal.

### P1 — Identidad estable: solo UID, nunca ID
Microsoft Project tiene dos identificadores: `ID` (número de fila, **se corre** al insertar/borrar/ordenar)
y `UniqueID` (estable de por vida). El footgun clásico: el agente lee la tarea `ID 45`, inserta una tarea,
y su siguiente write pega en la tarea equivocada.

**Regla:** todos los tools aceptan y devuelven `uid`. El `id` de fila aparece solo como campo de
display, nunca como argumento. Ningún write acepta `id`.

### P2 — Escritura verificada
Un write nunca reporta trabajo que no verificó. Después del commit se **relee del modelo** y se compara
contra lo pedido. Si no coincide → aparece en `rejected`, no en `applied`. El conteo sale de la relectura,
no de "la llamada no lanzó excepción".

Esto importa muchísimo en Project porque **ignora writes en silencio** todo el tiempo:
- Escribir `finish` en una tarea auto-programada → lo recalcula el motor y lo pisa.
- Escribir fechas contra un `constraint` duro → se ignora.
- Escribir en un campo calculado (`cost` con recursos asignados) → se ignora.
- Escribir en tarea resumen → se ignora.

### P3 — Radio de impacto antes de escribir
Cambiar una duración puede mover 400 tareas. Todo write acepta `dry_run: true`, que ejecuta la
simulación **de verdad**: copia el documento → aplica → recalcula con el motor real → hace el diff →
descarta la copia. No es una predicción estática.

El diff siempre responde tres cosas: cuántas tareas se mueven, cuánto se corre la fecha fin del
proyecto, y si cambia la ruta crítica.

### P4 — Disciplina de tokens
Un cronograma de 2.000 tareas en JSON son ~400k tokens. Muerte por contexto.
- Toda query pagina (`limit` por defecto 50) y devuelve `{total, returned, cursor}`.
- Field selection: set compacto por defecto, `fields:[...]` para pedir más.
- **El análisis se calcula en el servidor.** El agente nunca debe pedir 2.000 tareas para sacar la ruta
  crítica. Ese es el mayor ahorro de tokens del diseño.

### P5 — Un solo camino para cada cosa
Nada de `add_task` + `bulk_add_tasks` + `update_task` + `bulk_update_tasks`. Un `tasks_write` que
recibe una lista de ops tipadas. Batch de 1 es un caso de batch de N.

### P6 — Backend dual, superficie única — **MPXJ.NET primario, COM opcional**
> Revisado el 2026-08-03 tras validar en la máquina objetivo. Ver §8 para la evidencia.

Adaptador **MPXJ.NET** (por defecto: headless, cross-platform, multiformato, escritura MSPDI,
**sin Java y sin MS Project**) y adaptador **COM** (opcional: escritura .mpp nativa y motor de
cálculo real, solo donde COM realmente arranca).

El diseño original tenía COM como primario y MPXJ como fallback. **Se invirtió**: en la máquina
objetivo, con Project Professional 16.0.20228 instalado y bien registrado, COM falla
(`CO_E_SERVER_EXEC_FAILURE`). Un servidor cuyo camino principal no arranca en la máquina de su
propio autor no llega a 10 estrellas.

`project_health` declara qué backend está activo y su matriz de capacidades; los tools no soportados
en ese backend fallan con un mensaje explícito, nunca degradan en silencio.

---

## 1. Sesión (3 tools)

### `project_health`
Primera llamada de toda sesión. Sin argumentos.
```jsonc
{
  "backend": "com",                    // com | mpxj
  "ms_project": "16.0.17928 (Project Professional 2024)",
  "documents_open": [
    { "handle": "d1", "name": "Torre-A.mpp", "path": "...", "dirty": true, "tasks": 1284 }
  ],
  "active": "d1",
  "capabilities": {
    "write_native_mpp": true, "level_resources": true, "timephased": true,
    "earned_value": true, "whatif_simulation": true, "multi_project": true
  }
}
```

### `project_open`
```jsonc
{ "path": "...", "mode": "readwrite|readonly", "attach_if_open": true }
```
Devuelve `handle` + **`fingerprint`** (mtime + revisión interna + hash de conteos). Todo write posterior
revalida el fingerprint: si el documento cambió por fuera (alguien lo tocó en la GUI), el write **falla**
en vez de pisar. Esto resuelve el problema de proxy COM stale que sufre la competencia.

### `project_save`
```jsonc
{ "handle":"d1", "op": "save|save_as|close", "path": "...", "format": "mpp|mspdi|mpx", "keep_open": true }
```
`close` exige `save` explícito o `discard_changes: true`. Nunca guarda por su cuenta.

---

## 2. Lectura (5 tools)

### `tasks_query` — el caballo de batalla
```jsonc
{
  "handle": "d1",
  "filter": {
    "uids": [12, 45],
    "wbs_prefix": "1.2",
    "text": "cimentación",              // busca en nombre y notas
    "critical": true,
    "milestone": true,
    "status": "not_started|in_progress|complete|late|overdue",
    "resource": "Juan Pérez",
    "date_range": { "field": "finish", "from": "2026-09-01", "to": "2026-10-31" },
    "custom": { "Text1": "CWA-3" },
    "has_deadline": true,
    "mode": "manual|auto"
  },
  "shape": "flat|wbs|leaf_only|summary_only",
  "fields": ["uid","name","start","finish","duration","pct_complete","critical","total_float"],
  "sort": "start|finish|float|wbs",
  "limit": 50, "cursor": null
}
```
Campos por defecto (compactos): `uid, wbs, name, start, finish, duration, pct_complete, critical`.
`mode` (manual/auto) viaja **siempre**, aunque no se pida: sin él, el agente no puede razonar sobre
qué writes van a pegar.

### `links_query`
El grafo de dependencias es una forma distinta al listado de tareas, y casi siempre se quiere el
**subgrafo alrededor de N tareas**, no el grafo completo.
```jsonc
{ "handle":"d1", "uids":[45], "direction":"pred|succ|both", "depth": 2, "include_lags": true }
```
Devuelve aristas `{from, to, type: FS|SS|FF|SF, lag, lag_units, is_critical}` + los nodos tocados.
`depth: 0` = solo vecinos inmediatos. Con `driving_only: true` devuelve únicamente la cadena que
realmente empuja la fecha (la longest path), que es lo que uno quiere el 90% de las veces.

### `resources_query`
```jsonc
{ "handle":"d1", "filter": {"overallocated": true, "type":"work|material|cost", "name":"..."},
  "include": ["assignments","availability","rates","calendar"], "limit": 50 }
```

### `project_info`
Propiedades, fechas de arranque/fin, status date, estadísticas, calendarios definidos, líneas base
presentes (cuáles de las 11 tienen datos y de qué fecha), campos personalizados **con su alias**
(crítico: nadie sabe qué es `Text1` sin el alias), y conteo de tareas por modo/estado.

### `timephased_query`
Separado a propósito: es la llamada cara. Curvas de trabajo/costo por período.
```jsonc
{ "handle":"d1", "uids":[...], "measure":"work|cost|actual_work|baseline_work",
  "granularity":"day|week|month", "from":"...", "to":"...", "roll_up": "task|resource|project" }
```
Con guardas duras: rechaza rangos que generarían más de N celdas y sugiere subir la granularidad.

---

## 3. Análisis (3 tools) — calculado en el servidor

Aquí está el ahorro grande de tokens. El agente pregunta, no descarga.

### `schedule_analyze`
```jsonc
{ "handle":"d1", "aspects": ["critical_path","float_distribution","longest_path",
                             "overallocation","milestones","dependency_health"] }
```
Devuelve resúmenes ya digeridos: cadena crítica con sus UIDs, histograma de holgura, cuellos de
botella de recursos con fechas y magnitud del sobrecupo, tabla de hitos con estado vs deadline.

### `schedule_qa` — el que nadie tiene para Project
Chequeos **DCMA-14** (el estándar de la industria; los MCP de Primavera lo tienen, los de Project no)
más reglas propias:

| # | Chequeo | Umbral |
|---|---|---|
| 1 | Lógica faltante (sin predecesor o sin sucesor) | ≤ 5% |
| 2 | Lags positivos | ≤ 5% |
| 3 | **Lags negativos (leads)** | 0% |
| 4 | Tipos de relación: FS dominante | ≥ 90% |
| 5 | Constraints duros (MSO/MFO) | ≤ 5% |
| 6 | Tareas con float alto (> 44d) | ≤ 5% |
| 7 | Float negativo | 0% |
| 8 | Duraciones altas (> 44d) | ≤ 5% |
| 9 | Fechas inválidas (actual en el futuro / forecast en el pasado) | 0 |
| 10 | Recursos asignados | — |
| 11 | Missed tasks vs baseline | ≤ 5% |
| 12 | Critical Path Test (inyecta 600d y verifica que la fecha fin se corra) | pasa/falla |
| 13 | CPLI (índice de longitud de ruta crítica) | ≥ 0.95 |
| 14 | BEI (baseline execution index) | ≥ 0.95 |

Reglas Horizun encima: tareas sin código de presupuesto, hitos con duración ≠ 0, tareas huérfanas
fuera del WBS, nombres duplicados, tareas manuales en un cronograma que debería ser auto.

Salida: findings con `severity`, `rule`, `uids` afectados y `fix_hint` accionable. Con
`fix: true` genera el batch de ops para `tasks_write` que corrige lo automatizable.

### `baseline_compare`
Varianza contra la línea base N + **EVM completo**: BCWS/BCWP/ACWP, SV, CV, SPI, CPI, EAC, TCPI.
Por proyecto, por rama del WBS, o por recurso. Es lo que alimenta el dashboard.

---

## 4. Escritura (5 tools) — todas verificadas, todas con `dry_run`

Firma común de respuesta:
```jsonc
{
  "applied": 12,
  "rejected": [
    { "uid": 45, "field": "finish", "requested": "2026-09-10",
      "actual": "2026-09-14",
      "reason": "task is auto-scheduled; finish is engine-calculated. Set mode=manual or adjust duration/predecessors." }
  ],
  "impact": {
    "tasks_moved": 312,
    "project_finish": { "before": "2027-03-14", "after": "2027-03-28", "delta_days": 14 },
    "critical_path_changed": true,
    "new_negative_float": 8
  },
  "verified_by": "reread"
}
```

### `tasks_write`
```jsonc
{ "handle":"d1", "dry_run": false, "ops": [
  { "op":"create", "after_uid":44, "outline_level":3, "name":"Vaciado losa N+3",
    "duration":"5d", "mode":"auto" },
  { "op":"update", "uid":45, "set": { "duration":"8d", "pct_complete":40, "Text1":"CWA-3" } },
  { "op":"delete", "uid":99 },
  { "op":"move", "uid":50, "after_uid":45 },
  { "op":"outline", "uid":51, "indent": 1 }
]}
```
Un solo tool cubre lo que la competencia parte en 8. Progreso (`pct_complete`, `actual_start`,
`actual_finish`, `actual_work`, `remaining_duration`) es un `update` normal.

### `links_write`
```jsonc
{ "handle":"d1", "dry_run":false, "ops":[
  { "op":"link", "from":44, "to":45, "type":"FS", "lag":"2d" },
  { "op":"unlink", "from":44, "to":46 },
  { "op":"relink", "from":47, "to":48, "type":"SS", "lag":"0d" }
]}
```
Rechaza ciclos **antes** de aplicar, con la cadena del ciclo en el error.

### `resources_write`
Recursos + asignaciones + tarifas en un batch: `create|update|delete|assign|unassign|set_rate`.

### `calendars_write`
Calendarios, excepciones (feriados, paros de obra, temporada de lluvias), jornadas laborales,
asignación de calendario a proyecto/tarea/recurso.

### `schedule_update` — operaciones de motor (sin payload por tarea)
```jsonc
{ "handle":"d1", "op":"save_baseline|clear_baseline|set_status_date|
                       reschedule_incomplete|level_resources|recalculate",
  "baseline": 0, "status_date":"2026-08-01", "scope":"project|selection", "uids":[...] }
```
`level_resources` y `reschedule_incomplete` son las dos que más daño hacen sin querer → `dry_run`
obligatorio la primera vez (el server exige verlo antes de aplicar en firme).

---

## 5. Interop (4 tools) — el diferenciador

### `project_export`
`xlsx | csv | json | mspdi | pbip_dataset`. El `pbip_dataset` sale con el esquema que ya consume
nuestro Power BI (tareas, asignaciones, EVM, curva S), no un volcado crudo.

### `project_import`
Flujo **plan → apply**: lee el xlsx/csv, lo empareja contra el cronograma, devuelve el plan de cambios
(altas/bajas/modificaciones con diff campo a campo) y espera confirmación antes de aplicar.
Nunca importa a ciegas.

### `bim_link` *(fase 2)*
Mapea tareas ↔ elementos del modelo por código (`HRZ_COD_PRES` / PRODESA CLASS).
`op: suggest|get|set|clear`. `suggest` propone el emparejamiento con % de confianza y deja los dudosos
en una lista de revisión — mismo patrón que ya usamos para codificar keynotes.

### `bim_sync` *(fase 2)*
El puente 4D/5D en los dos sentidos:
- **Modelo → cronograma:** cantidades reales por elemento → recalcula duraciones por rendimiento.
- **Cronograma → modelo:** escribe fechas de inicio/fin en parámetros de los elementos de Revit para
  animación 4D y para el semáforo de avance en Navisworks/Power BI.

Esto no lo tiene nadie en el mercado. Es la razón por la que alguien elegiría este servidor.

---

## 6. Conteo final

| Bloque | Tools |
|---|---|
| Sesión | 3 |
| Lectura | 5 |
| Análisis | 3 |
| Escritura | 5 |
| Interop | 4 |
| **Total** | **20** |

Contra 79 de elsahafy y 49 de jeffmodeler — con **más** cobertura real (ellos no tienen QA DCMA,
ni escritura verificada, ni simulación real, ni puente BIM).

---

## 7. Validación de entorno (2026-08-03) — evidencia medida

Todo lo de abajo se ejecutó en la máquina objetivo (Windows 11). No es teoría.

### Runtimes disponibles

| Runtime | Estado |
|---|---|
| Python | 3.14.3 ✅ |
| uv | 0.11.28 ✅ |
| .NET | 10.0.301 ✅ |
| **Java** | ❌ **no instalado** |
| **MS Project** | Professional 16.0.20228.20124 ✅ / COM roto al empezar, **resuelto** — ver abajo |

### Lo que rompe el plan original

**1. COM no arrancaba — y la causa quedó cerrada.** `New-Object -ComObject MSProject.Application` →
`0x80080005 CO_E_SERVER_EXEC_FAILURE`. Se descartó una por una:
- Clase no registrada — el `LocalServer32` apunta correcto a `...\Office16\WINPROJ.EXE`.
- El sandbox del shell — fallaba igual fuera del sandbox.
- Desajuste de integridad/sesión — mismo usuario, misma sesión 1, sin elevación.
- Que Project no estuviera vivo — se lanzó `WINPROJ.EXE` (PID 41240, respondiendo) y
  `CoCreateInstance` **siguió fallando** con la app corriendo. `GetActiveObject` tampoco
  enganchaba: `0x800401E3 MK_E_UNAVAILABLE` (no se registra en la ROT).

**La causa era el primer arranque incompleto.** Ese lanzamiento interactivo de `WINPROJ.EXE`
completó el first-run de Office, y a partir de ahí COM quedó operativo de forma permanente:
tanto desde .NET (`status: "operational"`, build 16.0.20228) como desde PowerShell, que antes
fallaba. Es exactamente el paso 1 de reparación que `project_health` emite para `0x80080005`.

Lo que esto deja demostrado no es que COM sea inservible, sino que **COM puede estar caído en una
máquina con Project correctamente instalado y registrado, por una causa que ningún competidor
diagnostica** — devuelven el `COMException` crudo. Ese es el argumento de peso de `project_health`.

**2. MPXJ vía Python exige Java**, que aquí no hay. El `pip install mpxj` de la competencia
(albertito1998, jeffmodeler con el extra `[mpp]`) **no arranca en esta máquina**. Este sí es un
bloqueo estructural, no una condición transitoria.

### La salida: MPXJ.NET (IKVM) — probado, funciona

[MPXJ.Net 16.1.0](https://www.nuget.org/packages/MPXJ.Net) traduce MPXJ a un assembly .NET con
IKVM en tiempo de build. **Cero JVM.** Probado con un round-trip real:

```
=== MPXJ.Net probe ===
Runtime: .NET 8.0.28
JAVA_HOME: '<unset>'
built: tasks=4 resources=1 assignments=1
WRITE ok -> probe.xml (9125 bytes)
READ ok  -> tasks=4 resources=1 assignments=1
  [PASS] project name        [PASS] t1 resuelta por UID
  [PASS] t1 duration 5.0d    [PASS] t3 milestone flag
  [PASS] summary 3 children
=== RESULT: 5/5 checks passed ===
```

Se construyó un cronograma desde cero (resumen + 3 tareas + hito + recurso + asignación), se
escribió MSPDI y se releyó verificando **resolución por UniqueID** (principio P1) y supervivencia
de jerarquía, duración y flag de hito. Sin Java, sin abrir MS Project.

Costos medidos: **primer build ~3 min** (traducción IKVM, se cachea), builds siguientes normales.
352 warnings `IKVM0100/0101` son ruido esperado de clases opcionales no usadas — no afectan.

### Consecuencias de diseño

1. **Backend por defecto = MPXJ.NET; COM es acelerador auto-detectado.** El servidor pasa a ser
   .NET, no Python — mismo stack que el add-in de Revit y distribución como binario único. MPXJ.NET
   es el piso que siempre funciona; COM se activa **solo cuando se ha probado que arranca**, nunca
   por el hecho de estar registrado.
2. **"Zero prerequisites" es el titular del README**, y es cierto y demostrable: ni Java, ni MS
   Project, ni licencia. Los 6 competidores exigen al menos uno de los tres.
3. **`project_health` tiene que ser un doctor de verdad**, no un ping: detectar Project instalado,
   probar si COM realmente arranca, y si falla dar el diagnóstico exacto y el camino de reparación,
   en vez de un stacktrace COM. Ya está implementado y **ya se validó contra un fallo real**: el
   `0x80080005` de esta máquina se resolvió siguiendo el paso 1 que emite.
4. **Reponer lo que se pierde sin COM:** MPXJ no trae motor de recálculo de MS Project. `dry_run`
   real, `level_resources` y `reschedule_incomplete` exigen un **scheduler propio** (CPM forward/backward
   pass con calendarios y constraints). Es la pieza de ingeniería más grande del proyecto y hay que
   presupuestarla explícitamente — o declararla como capacidad solo-COM en `project_health`
   (que es lo que hace hoy la matriz de capacidades).

---

## 7.1 Estado de implementación — **completo**

Las 20 tools implementadas en `src/HorizunProjectMcp/`, sobre el SDK oficial
[`ModelContextProtocol` 2.0.0](https://www.nuget.org/packages/ModelContextProtocol).
**57/57 checks de aceptación en verde.**

| Pieza | Estado |
|---|---|
| Host + transporte stdio | ✅ logs a stderr; stdout es del protocolo |
| `Diagnostics/` — detección de COM y doctor de entorno | ✅ validado contra un fallo real |
| `Backends/` — I/O MPXJ, puente COM, sesiones con fingerprint | ✅ |
| `Analysis/CpmScheduler` + `WorkingCalendar` — motor de ruta crítica | ✅ pases adelante/atrás, holguras |
| `Analysis/Dcma14` — 14 checks + reglas Horizun | ✅ el test 12 inyecta y mide de verdad |
| `Analysis/EarnedValue` — EVM completo | ✅ BCWS/BCWP/ACWP, SPI, CPI, EAC, TCPI |
| `Writes/WriteEngine` — contrato de escritura verificada | ✅ por operación, no por campo |
| `Bim/` — emparejamiento por código y puente 4D | ✅ ambas direcciones |
| `Tools/` — las 20 tools | ✅ |
| Empaquetado como `dotnet tool` global | ✅ instalado y verificado |
| `tools/acceptance-test.py` | ✅ 57/57 |
| `tools/scheduler-test.py` | ✅ 22/22 |
| `tools/smoke-test.py` | ✅ 13/13 |

**92 checks en total**, sobre JSON-RPC real contra el servidor corriendo.

```bash
cd src/HorizunProjectMcp && dotnet build && cd ../..
python tools/acceptance-test.py && python tools/scheduler-test.py && python tools/smoke-test.py
```

### Tres bugs del motor CPM que la prueba de scheduler destapó

Solo se habían ejercitado relaciones FS. Al cubrir el resto salieron:

1. **Holgura −1 en toda la cadena crítica.** El pase hacia atrás se anclaba en
   `ProjectProperties.FinishDate`, que MPXJ **deriva de las fechas de las tareas** — o sea, el valor
   de la corrida anterior. El cronograma peleaba contra su propia respuesta previa. Ahora ancla en la
   fecha fin recién calculada; la holgura negativa viene de deadlines y constraints, como en Project.
2. **Las excepciones de calendario no hacían nada.** `AddDefaultBaseCalendar()` crea el calendario
   pero no lo asigna como predeterminado, así que `calendars_write` no tenía dónde ponerlas.
3. **Semántica de SF mal aserida en la prueba** (el piso de fecha de inicio decidía, no la relación).

La prueba no comprueba que responda: **verifica los contratos del diseño** — que un dry run no
comprometa nada, que un ciclo se rechace antes de aplicarse, que ninguna tarea caiga en fin de
semana, que el test 12 de DCMA mueva la fecha fin exactamente lo inyectado, y que las capacidades
que el backend no puede honrar se rechacen en vez de aproximarse.

### Lo que cambió respecto al diseño original

1. **Hay motor CPM propio.** El diseño lo daba como "la pieza de ingeniería más grande, hay que
   presupuestarla". Está hecho: sin él, MPXJ no programa nada — una tarea creada por su API no tiene
   fechas — y quedaban vacíos las fechas, la holgura, el EVM y todo el `dry_run`. Eso convierte
   `recalculate`, `dry_run_simulation`, `reschedule_incomplete` y el test 12 de DCMA en capacidades
   reales de **ambos** backends.
2. **`applied` cuenta operaciones, no campos.** Una operación a medias es una operación rechazada.
   El detalle por campo va aparte en `fieldsVerified`.
3. **`write_native_mpp` se delega a Project bajo demanda**, no exige que toda la sesión corra en COM.
   Verificado: .mpp real de 242 KB escrito desde el backend de archivo.
4. **Solo dos capacidades siguen en `false`** y se rechazan explícitamente: `write_native_mpp` sin
   Project instalado, y `level_resources` — cuyo heurístico es inédito, así que imitarlo sería dar
   otra respuesta con el mismo nombre.
5. **`project_open` puede crear** un cronograma vacío. La superficie lo implicaba y faltaba.

---

## 8. Cómo se llega a las 10 estrellas

El diseño no da estrellas por sí solo. El top del mercado tiene 7 y se gana con poco:

1. **Cero prerrequisitos — y es literal, no marketing.** Binario .NET único: sin Java, sin MS
   Project, sin licencia. Los 6 competidores exigen al menos uno de los tres, y en la máquina donde
   se hizo este benchmark **ninguno de ellos arrancaría** (§7). Ese es el titular del README, y es
   el argumento más fuerte que tenemos.
2. **README con un GIF de 20 segundos**: "corré el QA DCMA-14 sobre tu cronograma y mirá qué sale".
   Ese es el gancho, no la lista de tools.
3. **Listarlo en los 4 registries**: MCP Registry oficial, Glama, Smithery, PulseMCP. Ahí es donde
   la gente busca; ninguno de los 6 competidores está bien listado.
4. **Apuntar al término que la gente busca de verdad**: "DCMA 14-point check" y "4D BIM schedule".
   Hay demanda de planificadores de construcción y no hay oferta.
5. **Un caso real publicado**: cronograma de obra + modelo Revit + tablero Power BI, punta a punta.
   Es lo que ninguno de los 6 puede mostrar.
