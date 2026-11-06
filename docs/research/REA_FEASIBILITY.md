# Informe de viabilidad: REA (Reverse Engineer Anything)

**Fecha:** 2026-11-06 · **Autor:** sesión de desarrollo asistida (Claude Code) · **Estado:** cerrado para el MVP

## Verificación del upstream

| Comprobación | Resultado |
|---|---|
| Repositorio | `https://github.com/morluto/rea` (README descargado y leído desde `raw.githubusercontent.com/morluto/rea/HEAD/README.md`) |
| Licencia declarada | MIT (insignia y archivo `LICENSE` referenciados en el README) |
| Paquete | `rea-agents` en npm, instalación indicada: `npx rea-agents setup` |
| Advertencia del propio proyecto | Obtener REA solo de `morluto/rea`, `rea.tools` o el paquete npm `rea-agents`; nombres parecidos no son oficiales |
| Qué hace | Servidor MCP que conecta al agente con herramientas de análisis de binarios nativos (Hopper, Ghidra, IDA, Kuna), apps JavaScript/Electron, ensamblados .NET y webs |

## Qué modificaría `npx rea-agents setup`

Según su documentación, el *setup* registra REA en la configuración global del agente, instala instrucciones de flujo de trabajo y puede ofrecer instalar Hopper. Es decir, **cambia la configuración global del agente y puede instalar software de terceros**. El prompt del proyecto exige no ejecutar instaladores a ciegas y pedir autorización para cambios globales.

## Decisión

**No se ha instalado ni ejecutado REA.** Motivos:

1. **No hay objeto de estudio legítimo disponible.** El entorno es un contenedor Linux sin macOS, sin Apple Fotos instalado y sin licencia de Apple Fotos. Sin acceso legítimo a la aplicación no hay nada que inspeccionar; simular un análisis sería inventar evidencia.
2. **Coste/riesgo frente a beneficio.** El setup altera la configuración global del agente. No aporta valor si no hay binario que analizar.
3. **No es necesario.** Los comportamientos a recrear (navegación por biblioteca, álbumes, edición no destructiva, Eliminados recientemente…) están descritos en la guía pública de Apple Fotos y son patrones de producto genéricos. Se implementan con diseño propio, sin necesidad de conocer la implementación interna de Apple.

## Conclusiones obtenidas con REA

Ninguna. **Ningún documento de este repositorio debe presentarse como resultado de ingeniería inversa.** Las decisiones de producto se basan en:

- Documentación pública de Apple Fotos para Mac (comportamiento observable descrito por Apple).
- Convenciones de Windows/Fluent Design.
- Proyectos abiertos revisados (ver `OPEN_SOURCE_REVIEW.md`).

## Cuándo reconsiderarlo

Solo si el propietario del repositorio dispone de un Mac con Apple Fotos con licencia y quiere estudiar un comportamiento concreto observable (por ejemplo, la agrupación de recuerdos). En ese caso: instalar desde el upstream verificado, con autorización explícita, registrar cada conclusión con aplicación/versión, método, evidencia, confianza y diferencias deliberadas, y nunca reutilizar código ni recursos de Apple. Photos Windows no dependerá de REA en ningún caso.
