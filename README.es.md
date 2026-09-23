# Horizun Project MCP

[![build and test](https://github.com/HorizunGroup/horizun-msproject-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/HorizunGroup/horizun-msproject-mcp/actions/workflows/ci.yml)

**Un servidor MCP para Microsoft Project que no necesita ni Java ni Microsoft Project — y que dice
la verdad sobre lo que escribió.**

*[English](README.md)*

Apunta cualquier cliente MCP a un `.mpp`, un `.xer` de Primavera o un `.xml` MSPDI y hazle
preguntas de verdad: por dónde pasa la ruta crítica, qué recursos están sobreasignados, si el
cronograma sobreviviría una auditoría DCMA, qué le hace al fin de obra un atraso de dos semanas.
Después escribe los cambios de vuelta — y sabes cuáles quedaron, porque cada uno se vuelve a leer
del modelo antes de reportarlo.

```bash
dotnet tool install -g HorizunMsProjectMcp
```

Esa es toda la instalación. Sin JVM. Sin Microsoft Project. Sin licencia. Lee y programa solo.

[![NuGet](https://img.shields.io/nuget/v/HorizunMsProjectMcp.svg)](https://www.nuget.org/packages/HorizunMsProjectMcp)

### Conectar un cliente

| Cliente | Qué hacer |
|---|---|
| **Claude Desktop** | Descarga el `.mcpb` de [Releases](https://github.com/HorizunGroup/horizun-msproject-mcp/releases/latest) y suéltalo en **Configuración → Extensiones**. Él instala el servidor: no hay que correr nada antes. |
| **Claude Code** | `claude mcp add horizun-msproject-mcp -- horizun-msproject-mcp` |
| **Codex** | `codex mcp add horizun-msproject-mcp -- horizun-msproject-mcp` |
| **ChatGPT** | Necesita un puente HTTPS delante del servidor stdio. [Cómo, y qué sopesar antes.](docs/INSTALL.md#chatgpt) |

Las instrucciones completas por cliente están en [docs/INSTALL.md](docs/INSTALL.md).

---

## Por qué este

Hay varios MCP de Microsoft Project. Todos exigen Java, o un Microsoft Project licenciado, o un
driver JDBC de pago — y en la máquina donde se construyó este, **ninguno arrancó**. Ocho cosas de
aquí no existen en ningún otro:

| | |
|---|---|
| **Cero prerrequisitos** | Un solo binario .NET. MPXJ está compilado a .NET con IKVM, así que no hay JVM en ninguna parte, ni Microsoft Project tampoco. |
| **Las fechas de Project, no una imitación** | Donde Project está instalado, el propio Project calcula cada fecha que reporta este servidor. En siete cronogramas reales — 8.823 tareas — los recálculos y las ediciones hechos a través de él coincidieron con Project en todas las tareas, al minuto. [Cómo.](#las-fechas-las-de-microsoft-project) |
| **Escrituras verificadas** | Microsoft Project ignora escrituras en silencio todo el tiempo: fechas autoprogramadas, restricciones duras, resúmenes calculados, costos derivados. Aquí nada se reporta como aplicado hasta releerlo del modelo y compararlo. |
| **Evaluación DCMA de 14 puntos** | El estándar de la industria para decidir si un cronograma es ejecutable. Los MCP de Primavera lo implementan; ninguno de los de Microsoft Project. El chequeo 12 inyecta de verdad un atraso de 600 días y mide qué se movió. |
| **Recupera la lógica que nadie enlazó** | Un cronograma bien dibujado en la barra pero sin vínculos es el defecto más común que existe: DCMA lo señala y nadie lo arregla. Las fechas ya declaran el orden — esto lo lee de vuelta, verifica que el mismo orden se repita en cada repetición, y propone los vínculos faltantes. |
| **Reprogramación medida** | Cada opción de recuperación se aplica sobre una copia, se reprograma, y se reporta con la fecha de fin que realmente produce. Una opción que no recupera nada lo dice, en vez de ofrecerse como consejo. |
| **Aprende de tus proyectos pasados** | Apúntalo a cronogramas terminados y te dice cuánto duró de verdad cada actividad y qué suele precederla; después redacta un programa nuevo desde ahí, una tarea por unidad, secuenciado como los oficios se siguieron de verdad. |
| **Un puente BIM** | Amarra tareas a elementos del modelo por código, convierte cantidades medidas en propuestas de duración, y emite las fechas por elemento que alimentan el 4D en Navisworks y el tablero de avance en Power BI. Esto no lo hace nadie más. |

---

## Las 25 herramientas

**Sesión** — `project_health` · `project_open` · `project_save`

`project_health` es un médico, no un ping. Detecta Microsoft Project, prueba si su servidor COM
*realmente arranca*, y cuando no, devuelve el diagnóstico HRESULT y los pasos de reparación. No es
hipotético: esta máquina dio `CO_E_SERVER_EXEC_FAILURE` con un registro perfectamente válido, y el
primer paso de la ruta de reparación que emite es lo que lo arregló.

**Lectura** — `project_info` · `tasks_query` · `links_query` · `resources_query` · `timephased_query`

Filtrado, paginado, con selección de campos. Las tareas se direccionan por su `uid` estable; el `id`
de fila es solo para mostrar, porque se corre apenas insertas una tarea y direccionar por él edita
la tarea equivocada.

**Análisis** — `schedule_analyze` · `schedule_qa` · `baseline_compare`

Calculado en el servidor, para que el agente haga una pregunta en vez de traerse dos mil tareas al
contexto. Ruta crítica, distribución de holguras, cadena conductora, sobreasignación día a día,
DCMA-14 y valor ganado completo (BCWS/BCWP/ACWP, SPI, CPI, EAC, TCPI) — denominado en costo donde el
cronograma lo tiene, en horas-hombre donde tiene horas, y ponderado por duración donde no tiene
ninguno de los dos, que es la mayoría. El reporte dice cuál usó.

**Escritura** — `tasks_write` · `links_write` · `resources_write` · `calendars_write` · `schedule_update`

Por lotes, tipadas, verificadas. Los ciclos se rechazan antes de aplicarse, nombrando la cadena
culpable. Dos cosas que este backend no puede hacer no se ofrecen: reordenar una tarea dentro del
esquema, y editar el patrón semanal de horas de un calendario. Pedir cualquiera de las dos obtiene
un rechazo que la nombra y dice dónde hacerlo — una operación a medias es peor que una ausente.

**Planeación** — `schedule_recovery` · `schedule_target` · `schedule_sequence` · `schedule_learn` · `schedule_generate`

Reprogramación medida, no afirmada. `schedule_recovery` encuentra lo atrasado, lo ordena por cuánto
cronograma cuelga detrás, y prueba cada palanca — quitar retrasos en la cadena conductora, traslapar
entregas, comprimir las críticas más largas — sobre una copia desechable, y reporta la fecha de fin
que cada una produce de verdad. `schedule_target` pone a prueba una fecha que te entregaron y nombra
el trabajo que la red no sostiene. `schedule_sequence` recupera la lógica que falta leyendo el orden
que las propias fechas ya declaran. `schedule_learn` extrae de cronogramas terminados cuánto toma de
verdad cada actividad y qué suele ir antes; `schedule_generate` lo convierte en un primer borrador,
una tarea por apartamento o piso, secuenciado como la historia dice que se siguen los oficios.

Dale a `schedule_learn` una exportación del modelo junto con los cronogramas y mide **rendimientos**
— lo que una cuadrilla realmente sacó en un día — cruzando cantidades con tareas por el código
compartido. `schedule_generate` dimensiona entonces las duraciones con las cantidades del proyecto
nuevo, en vez de copiar una duración recordada, porque lo que viaja entre proyectos es el
rendimiento y lo que cambia es la cantidad. Entrega una exportación por cronograma, en el mismo
orden: los rendimientos se miden por proyecto, y sumar las cantidades entre proyectos los inflaría
todos.

Un borrador solo puede estar tan bien secuenciado como los cronogramas de los que aprendió. Donde
las fuentes enlazan cada actividad consigo misma unidad tras unidad pero nunca con los oficios
vecinos, ambas herramientas lo dicen y dan el número.

**Interoperabilidad** — `project_export` · `project_import` · `bim_link` · `bim_sync`

CSV, JSON, MSPDI, XER y PMXML de Primavera, `.mpp` nativo, y un dataset con forma para Power BI. Las
importaciones planean antes de escribir.

### Lo que cuesta tenerlo cargado

Las 25 herramientas presentan unos **8.400 tokens** de esquema, en cada prompt, mientras el servidor
esté conectado. Ese es el precio honesto de la superficie y conviene saberlo antes de decidir
cargarla. Es también la razón de que se quedara en 25: la alternativa más grande trae 79
herramientas, y pasado cierto punto un agente no sostiene su propia superficie en la cabeza lo
suficiente para elegir bien dentro de ella.

---

## Los dos contratos

**Nada se da por aplicado hasta verificarlo.** La respuesta de una escritura trae qué quedó, qué se
rechazó y por qué — con la razón, no un código — y el impacto medido sobre el cronograma: cuántas
tareas se movieron, cómo cambió la fecha de fin, si la ruta crítica cambió y cuánta holgura negativa
apareció.

**Una corrida en seco es una simulación real.** `dryRun: true` copia el cronograma en profundidad,
aplica el lote, lo reprograma con el motor de ruta crítica, mide la diferencia y bota la copia. Los
números de impacto son observados, no pronosticados.

---

## Honestidad de capacidades

`project_health` publica una matriz de capacidades, y las herramientas la respetan. Dos cosas se
quedan en `false` en el backend de archivo y **rechazan en vez de aproximar**:

- **`write_native_mpp`** — ninguna biblioteca puede autorar el formato binario. Donde Microsoft
  Project está instalado, el guardado se delega en él y obtienes un `.mpp` genuino; donde no, te da
  MSPDI y una explicación.
- **`level_resources`** — la heurística de nivelación de Microsoft Project no está publicada.
  Cualquier imitación sería otra respuesta con el mismo nombre.

Guardar encima de una línea base que ya tiene datos también se rechaza, salvo que lo pidas
explícitamente. La línea base es el registro del plan original contra el que se mide toda variación,
y después no se puede recuperar del archivo.

## Las fechas: las de Microsoft Project

**Donde Microsoft Project está instalado, las fechas las calcula Microsoft Project.** Recalcular,
reprogramar después de una escritura, las corridas en seco, las opciones de recuperación, las fechas
objetivo y la prueba de ruta crítica del DCMA le entregan el cronograma a Project, lo dejan calcular
y toman de vuelta las fechas que él calculó. `project_health` lo reporta como
`schedulingEngine: microsoft-project`.

Medido, no supuesto. En siete cronogramas reales de obra — de 21 a 5.984 tareas, 8.823 en total
contando resúmenes, con tareas divididas, trabajo en curso, jornadas de 9 horas y medio sábado —
recalcular a través de este servidor reprodujo las fechas que calcula Microsoft Project en **todas las
tareas de todos los cronogramas, al minuto**, y dejó el avance exactamente como lo tenía Project. Las escrituras se midieron igual: un cambio de duración, un porcentaje de avance o un
vínculo nuevo hecho por este servidor, y el mismo cambio hecho a mano en Project, dieron las mismas
fechas y el mismo avance tarea por tarea, resúmenes incluidas.

Llegar ahí exigió más que abrir el archivo en Project. La importación de XML del propio Project no
reproduce a Project: en un cronograma real de 126 tareas, volver a abrir incluso **su propia**
exportación XML puso todas las tareas en otras fechas. Estas son las causas, y la entrega a Project
resuelve cada una:

- **Tareas sin recurso.** Una tarea sin recurso igual lleva una asignación de relleno, y al importarla
  Project la programa con el calendario de ese recurso de relleno en vez del de la tarea. Se omite
  cuando no dice nada que la tarea no diga ya.
- **Tareas divididas.** El trabajo que empieza, se pausa y sigue guarda sus pausas solo en el reparto
  día por día de la asignación, así que ese reparto viaja también.
- **Trabajo en curso.** Una tarea en curso vuelve de la importación XML con parte de lo trabajado
  pasado a lo restante. Por eso el avance nunca se toma de ese viaje: cuando una escritura lo cambia,
  cambia con las reglas de Project — una duración mayor conserva lo ya trabajado, un porcentaje nuevo
  arranca o termina la tarea — y las resúmenes lo consolidan como lo hace Project.
- **Filas en blanco.** Una fila insertada en Project y dejada vacía no es una tarea para Project, pero se
  lee como una fechada al inicio del proyecto, y una resumen de un cronograma real creció dos años hacia
  atrás para alcanzarla. Viaja como la fila en blanco que es.

Recalcular el cronograma de 4.753 tareas toma unos 20 segundos, casi todo dentro de Project.

**Si tienes Project abierto**, se usa tal como está. Microsoft Project es de instancia única —
cualquier automatización, incluida la de este servidor, cae en la copia que estás usando —, así que
este servidor nunca oculta tu ventana, nunca recalcula ni cierra tus documentos, y solo toca una copia
temporal que abrió él mismo, verificando antes de cada paso que el documento activo siga siendo esa
copia. Quedas en el documento que tenías activo.

### Sin Microsoft Project

MPXJ lee y escribe archivos de cronograma pero no los *programa*, así que también hay un motor de ruta
crítica propio — pasada hacia adelante y hacia atrás, holgura total y libre, los cuatro tipos de
relación, retrasos, restricciones, fechas límite, fechas reales y el calendario de cada tarea. Es el
que corre en macOS, Linux y los Windows sin Project.

> ### ⚠️ Ese motor no es el programador de Microsoft Project
>
> Reproduce a Project exactamente en cronogramas construidos con este servidor, no en los importados:
> en cuatro archivos reales coincidió con las fechas de inicio de Project en el 100%, 69%, 23% y 23% de
> las tareas. No implementa tipos de tarea, programación condicionada por el esfuerzo, fechas
> impulsadas por recursos, tareas manuales o divididas, ni duraciones transcurridas. Por eso, sin
> Project, un cronograma importado nunca se reprograma en silencio — sus fechas siguen siendo las de
> Project hasta que pidas las de este motor explícitamente con `schedule_update op='recalculate'`, que
> te advierte primero. La lectura, el análisis, el DCMA-14 y el valor ganado corren sobre las fechas
> que el archivo ya trae y no se ven afectados.

---

## Construir y probar desde el código

```bash
cd src/HorizunMsProjectMcp && dotnet build && cd ../..
python tools/acceptance-test.py   # 65 chequeos, las 25 herramientas de punta a punta
python tools/scheduler-test.py    # 45 chequeos, corrección del motor de ruta crítica
python tools/planning-test.py     # 52 chequeos, reprogramación y aprendizaje
python tools/robustness-test.py   # 34 chequeos, concurrencia y entrada hostil
python tools/smoke-test.py        # 13 chequeos, entorno y capacidades
python tools/packaging-test.py    # 33 chequeos, la metadata que lee cada cliente
python tools/project-engine-test.py  # 20 chequeos, Microsoft Project como motor (requiere Project)
```

**262 chequeos**, ejecutados sobre JSON-RPC real contra el servidor corriendo, en Windows y en
Linux. Con Microsoft Project instalado, las suites corren con Project calculando las fechas, y
`project-engine-test.py` compara el servidor contra Project haciendo lo mismo a mano — fechas, avance
y resúmenes, tarea por tarea — y verifica que un Project que tengas abierto quede exactamente como
estaba. El trabajo de Linux es la evidencia de la afirmación de portada: corre en una máquina sin JVM
y sin Microsoft Project.

La suite de robustez es la que importa para confiar en esto con un cliente real: cuarenta
escrituras en vuelo a la vez sobre el mismo documento, rutas hostiles, tamaños de página absurdos,
nombres no latinos. El modelo de objetos de MPXJ no es seguro entre hilos y un cliente MCP puede
encadenar llamadas sin esperar — sin protección, sesenta escrituras concurrentes fallaron todas y
dejaron el documento inservible. Hoy cada documento tiene su propio candado, reentrante porque
varias herramientas están construidas sobre otras.

Más allá de las suites, el servidor se condujo por el ciclo completo de trabajo de un programador
sobre dos cronogramas de obra en producción — uno de 5.985 tareas y otro de 170 MB y 1.937 tareas —
abriendo, auditando, sacando línea base, registrando avance, midiendo valor ganado y exportando el
dataset de Power BI.

---

## Formatos

**Lee** `.mpp` `.mpt` `.mpx` · MSPDI `.xml` · Primavera `.xer` `.pmxml` · Asta `.pp` · Planner ·
GanttProject y más, a través de [MPXJ](https://www.mpxj.org/).

**Escribe** MSPDI `.xml` (Microsoft Project lo abre nativamente) · `.mpx` · `.xer` y `.pmxml` de
Primavera · Planner · SDEF · JSON · CSV · dataset de Power BI · `.mpp` nativo donde Microsoft
Project esté instalado.

## Contribuir

[CONTRIBUTING.md](CONTRIBUTING.md) explica los contratos que un cambio tiene que mantener y cómo se
publica una versión. Los reportes de error y los pull requests son bienvenidos.

## Licencia

MIT. Ver [LICENSE](LICENSE) y [NOTICE](NOTICE) — MPXJ es LGPL-2.1-or-later y sus ensamblados viajan
dentro del paquete.
