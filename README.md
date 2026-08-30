# Actividad 3.3: Laboratorio de Observabilidad End-to-End con Microservicios en .NET 10

Este repositorio contiene la implementación completa de una arquitectura observable de microservicios basada en **.NET 10 Minimal API**, instrumentada con **OpenTelemetry SDK** (3 pilares: trazas distribuidas, métricas y logs estructurados correlacionados), capa centralizada de acceso a datos **DataAccess** con **EF Core Code-First** sobre **PostgreSQL**, y spans de base de datos con **OTel DB Semantic Conventions**.

---

## 📊 Resumen Ejecutivo y Estado de la Solución

- **Arquitectura de Microservicios Desacoplada**:
  - `Passengers` (:5001): Microservicio de negocio de pasajeros. Desacoplado de la base de datos; consume `DataAccess` vía REST con propagación de contexto W3C (`traceparent`).
  - `Checkin` (:5002): Microservicio de negocio de checkin de vuelos. Valida pasajeros contra `Passengers` y persiste registros en `DataAccess` vía REST.
  - `DataAccess` (:5003): Microservicio central de acceso a datos (.NET 10 Minimal API) con **EF Core Code-First** conectado a **PostgreSQL** (`observability_db`).
- **Base de Datos Unificada**:
  - Eliminado Couchbase por completo.
  - Persistencia relacional única en PostgreSQL con volumen persistente nombrado `postgres_data`.
- **Instrumentación OpenTelemetry Completa (3 Pilares)**:
  - **Trazas**: `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http` y spans semánticos de base de datos (`OpenTelemetry.Instrumentation.EntityFrameworkCore` y `Npgsql.OpenTelemetry` con `db.statement` y `db.system = "postgresql"`).
  - **Métricas**: `OpenTelemetry.Instrumentation.Runtime`, ASP.NET Core y métricas de negocio personalizadas vía `System.Diagnostics.Metrics`.
  - **Logs**: Formateador JSON estructurado (`OtelJsonConsoleFormatter`) enriquecido automáticamente con `trace_id` y `span_id`.
- **Backends de Observabilidad Locales**:
  - **Jaeger UI (:16686)**: Visualización y análisis de trazas distribuidas y spans de base de datos.
  - **Prometheus (:9090)**: Ingesta de métricas y evaluación de reglas de detección de anomalías AIOps.
  - **Grafana (:3000)**: Dashboards de Golden Signals correlacionados con Jaeger y Prometheus.
  - **Volúmenes Persistentes**: `postgres_data`, `prometheus_data`, `grafana_data`.

---

## 🏗️ Diagrama de Arquitectura del Sistema

```text
                           [ Cliente / Pruebas / k6 ]
                                        │
                      ┌─────────────────┴─────────────────┐
                      ▼                                   ▼
            [ Passengers Service ]               [ Checkin Service ]
             (HTTP REST :5001)                   (HTTP REST :5002)
                      │                                   │
                      │                                   ├───> (Valida Pasajero HTTP)
                      │                                   │
                      └─────────────────┬─────────────────┘
                                        ▼ (HTTP REST + W3C TraceContext)
                              [ DataAccess Service ]
                        (.NET 10 Minimal API - Puerto :5003)
                                        │
                                        │ (EF Core Code-First + OTel DB Spans)
                                        ▼
                                [ PostgreSQL :5432 ]
                               (Base: observability_db)
                                        │
           ┌────────────────────────────┴────────────────────────────┐
           │                  Telemetría OTLP (4317)                 │
           ▼                                                         ▼
   [ OTel Collector ] ────► [ Jaeger :16686 ] (Trazas Distribuidas & DB Spans)
           ├──────────────► [ Prometheus :9090 ] (Métricas & Reglas de Anomalías AIOps)
           └──────────────► [ Grafana :3000 ] (Dashboards & Correlación Trace-Log)
```

---

## 🚀 Puesta en Marcha en Entorno Local (Docker Compose)

### 1. Ejecutar Pruebas Unitarias
```bash
# Ejecutar todas las pruebas unitarias de la solución (.NET 10)
dotnet test
```

### 2. Construir y Levantar los Contenedores
```bash
docker compose up --build -d
```

### 3. Verificar el Estado de los Contenedores
```bash
docker compose ps
```

### 4. Enlaces de Acceso a Servicios
| Servicio / Herramienta | URL / Puerto | Descripción |
| :--- | :--- | :--- |
| **Passengers API** | `http://localhost:5001` | Microservicio de Pasajeros |
| **Checkin API** | `http://localhost:5002` | Microservicio de Check-in |
| **DataAccess API** | `http://localhost:5003` | Microservicio Central de Base de Datos |
| **Jaeger UI** | `http://localhost:16686` | Backend de Trazas Distribuidas y Spans de BD |
| **Prometheus** | `http://localhost:9090` | Métricas y Reglas de Alerta / AIOps |
| **Grafana UI** | `http://localhost:3000` | Dashboards de Observabilidad (`admin` / `admin`) |

---

## 🧪 Guía de Pruebas End-to-End

### A. Crear un Pasajero (a través de Passengers API)
```bash
curl -X POST http://localhost:5001/passengers \
  -H "Content-Type: application/json" \
  -d '{"id":"PAS-001","firstName":"Carlos","lastName":"Gomez","email":"carlos.gomez@example.com","passportNumber":"P1234567"}'
```

### B. Consultar el Pasajero Creado
```bash
curl http://localhost:5001/passengers/PAS-001
```

### C. Realizar Check-in (a través de Checkin API)
```bash
curl -X POST http://localhost:5002/checkin \
  -H "Content-Type: application/json" \
  -d '{"passengerId":"PAS-001","flightNumber":"AV204","seatNumber":"14A","baggageCount":1}'
```

### D. Consultar el Registro de Check-in
```bash
curl http://localhost:5002/checkin/CHK-<ID_GENERADO>
```

---

## 🔍 Verificación de los 3 Pilares de Observabilidad

### 1. Trazas Distribuidas y OTel DB Semantic Conventions en Jaeger (`http://localhost:16686`)
1. Ingresa a `http://localhost:16686`.
2. En **Service**, selecciona `checkin-service` y haz clic en **Find Traces**.
3. Selecciona la traza generada por el `POST /checkin`.
4. Observa el árbol distribuido de spans:
   ```text
   checkin-service: POST /checkin
   ├── checkin-service: ValidatePassengerHttp
   │   └── passengers-service: GET /passengers/{id}
   │       └── data-access-service: GET /passengers/{id}
   │           └── data-access-service: SELECT FROM "Passengers"  <-- DB Span
   └── checkin-service: PersistCheckinDataAccess
       └── data-access-service: POST /checkins
           └── data-access-service: INSERT INTO "CheckinRecords"  <-- DB Span
   ```
5. Al expandir los spans de base de datos generados por `data-access-service`, se visualizan los atributos estandarizados:
   - `db.system`: `"postgresql"`
   - `db.name`: `"observability_db"`
   - `db.statement`: consulta SQL parametrizada (`SELECT ...`, `INSERT ...`)
   - `net.peer.name`: `"postgres"`

### 2. Métricas y Golden Signals en Prometheus (`http://localhost:9090`)
- **Latencia P99**:
  ```promql
  histogram_quantile(0.99, sum(rate(http_server_request_duration_seconds_bucket[2m])) by (le))
  ```
- **Error Rate (5xx)**:
  ```promql
  rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[2m]) / rate(http_server_request_duration_seconds_count[2m])
  ```
- **Throughput (RPS)**:
  ```promql
  sum(rate(http_server_request_duration_seconds_count[2m])) by (service_name)
  ```

### 3. Logs Correlacionados por `trace_id`
Inspecciona los logs generados en consola:
```bash
docker compose logs data-access | grep trace_id
```
Cada registro de log en formato JSON estructurado incluye:
```json
{
  "timestamp": "2026-08-30T13:20:00.123Z",
  "level": "Information",
  "service": "data-access-service",
  "message": "DataAccess: Checkin CHK-001 saved in PostgreSQL for passenger PAS-001",
  "trace_id": "4bf92f3577b34da6a3ce929d0e0e4736",
  "span_id": "00f067aa0ba902b7"
}
```

---

## ⚡ Módulo B: Detección de Anomalías (AIOps) y Regla de Correlación Local

La regla de correlación de anomalías está configurada en `config/alerts.yml` y evaluada en Prometheus:

```yaml
- alert: CorrelatedErrorRateAndLatencyAnomaly
  expr: |
    (
      rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[2m]) 
      > 
      (
        avg_over_time(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[10m])[30m:1m]) 
        + 2 * stddev_over_time(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[10m])[30m:1m])
      )
    )
    and
    (
      histogram_quantile(0.99, sum(rate(http_server_request_duration_seconds_bucket[2m])) by (le)) > 0.200
    )
  for: 10s
```

### Cómo probar el disparo de la alerta localmente:
Ejecuta una ráfaga que inyecte latencia (>200ms) y fuerce errores 500:
```powershell
1..50 | ForEach-Object {
    Invoke-RestMethod -Uri "http://localhost:5002/checkin?delay=250&error=true" -Method Post -ContentType "application/json" -Body '{"passengerId":"PAS-001","flightNumber":"AV204","seatNumber":"14A","baggageCount":1}' -SkipHttpErrorCheck
    Start-Sleep -Milliseconds 100
}
```
1. Abre `http://localhost:9090/alerts`.
2. Verás la alerta `CorrelatedErrorRateAndLatencyAnomaly` en estado **FIRING**.
3. Haz clic en el enlace `runbook_url` para navegar a Jaeger y extraer el `trace_id` del request fallido.

---

## 💥 Módulo D: Experimentos de Caos Controlado

### Experimento 1: Inyección de Latencia en `Passengers` (200ms)
```powershell
1..30 | ForEach-Object {
    Invoke-RestMethod -Uri "http://localhost:5001/passengers/PAS-001?delay=200" -Method Get
    Start-Sleep -Milliseconds 100
}
```
* **Verificación**: Comprueba en Prometheus que `http_request_duration_seconds` supera el SLO y en Jaeger observa el span alargado en 200ms.

### Experimento 2: Tasa de Error 10% en `DataAccess`
```powershell
1..100 | ForEach-Object {
    $shouldFail = ($_ % 10 -eq 0)
    $errorParam = if ($shouldFail) { "?error=true" } else { "" }
    Invoke-RestMethod -Uri "http://localhost:5003/passengers/PAS-001$errorParam" -Method Get -SkipHttpErrorCheck
    Start-Sleep -Milliseconds 50
}
```
* **Verificación**: Comprueba que el Error Rate sube al 10% y las trazas correspondientes quedan etiquetadas con `error=true` en Jaeger.

---

## ☁️ Comandos `gcloud CLI` para la Futura Fase Cloud (Referencia Ordenada)

Para la siguiente etapa de despliegue en Google Cloud Platform (GCP Cloud SQL, GKE, Cloud Monitoring, Cloud Service Mesh y VPC Flow Logs):

```bash
# 1. Autenticación y configuración del proyecto
gcloud auth login
gcloud config set project <TU_PROJECT_ID>
gcloud config set compute/region us-central1

# 2. Habilitación de APIs de GCP
gcloud services enable \
    container.googleapis.com \
    sqladmin.googleapis.com \
    monitoring.googleapis.com \
    logging.googleapis.com \
    mesh.googleapis.com \
    securitycenter.googleapis.com

# 3. Asignación de Roles IAM al Service Account de la carga de trabajo
gcloud projects add-iam-policy-binding <TU_PROJECT_ID> \
    --member="serviceAccount:sa-observability@<TU_PROJECT_ID>.iam.gserviceaccount.com" \
    --role="roles/cloudsql.client"

gcloud projects add-iam-policy-binding <TU_PROJECT_ID> \
    --member="serviceAccount:sa-observability@<TU_PROJECT_ID>.iam.gserviceaccount.com" \
    --role="roles/monitoring.metricWriter"

gcloud projects add-iam-policy-binding <TU_PROJECT_ID> \
    --member="serviceAccount:sa-observability@<TU_PROJECT_ID>.iam.gserviceaccount.com" \
    --role="roles/cloudtrace.agent"

gcloud projects add-iam-policy-binding <TU_PROJECT_ID> \
    --member="serviceAccount:sa-observability@<TU_PROJECT_ID>.iam.gserviceaccount.com" \
    --role="roles/logging.logWriter"

# 4. Habilitación de VPC Flow Logs (Módulo C - Network Observability)
gcloud compute networks subnets update default \
    --region=us-central1 \
    --enable-flow-logs \
    --logging-aggregation-interval=INTERVAL_5_SEC \
    --logging-flow-sampling=0.5 \
    --logging-metadata=INCLUDE_ALL_METADATA
```
