# ADR-0008 · Personas, lugares y recuerdos

**Decisión.**
- Todo procesamiento «inteligente» será local, opcional y explicable; nada se envía a Internet.
- **Personas y mascotas** (detección/agrupación facial): aplazado a la Fase 8. Requiere elegir un modelo ONNX con licencia de pesos compatible y verificable; hasta entonces la sección no aparece en la interfaz (no hay botones ficticios). Los datos biométricos derivados se guardarán en el catálogo local con opción de borrado total.
- **Lugares**: las coordenadas GPS ya se leen, se muestran y se pueden editar/borrar; la búsqueda por área (`AssetQuery.Area`) existe. Sin geocodificación inversa remota. Mapa: solo offline u opt-in en una fase posterior.
- **Recuerdos**: reglas transparentes (mismo día en años anteriores, viajes por fecha+GPS); nunca se presentará como “comprensión” de la IA.
