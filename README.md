# Actividad 3.3: Observabilidad End-to-End en Microservicios .NET 10, GKE, OTel y Chaos Engineering

Este repositorio contiene la solución integral para el laboratorio de observabilidad distribuida, compuesta por tres microservicios en **.NET 10 Minimal API**, capa centralizada de persistencia **DataAccess** con **EF Core Code-First** sobre **PostgreSQL**, instrumentación de **OpenTelemetry SDK** (3 pilares: trazas, métricas y logs estructurados correlacionados), **spans semánticos de base de datos** (`OTel DB Semantic Conventions`), stack completo de observabilidad desplegado en **Google Kubernetes Engine (GKE Standard)**, detección de anomalías **AIOps**, observabilidad de red con **VPC Flow Logs**, y resiliencia con **Chaos Mesh**.

---

## 📊 Arquitectura del Sistema en GKE

```text
                                  [ Internet / Tráfico Externo ]
                                                 │
                      ┌──────────────────────────┴──────────────────────────┐
                      ▼                                                     ▼
        [ Passengers Microservice ]                               [ Checkin Microservice ]
        • K8s LoadBalancer Público (:5001)                        • K8s LoadBalancer Público (:5002)
        • HPA: 1 a 3 réplicas                                     • HPA: 1 a 3 réplicas
        • Namespace: apps                                         • Namespace: apps
                      │                                                     │
                      │                                 ┌───────────────────┘ (Valida Pasajero HTTP)
                      │                                 ▼
                      └────────────────────────► [ DataAccess Microservice ]
                                                 • K8s ClusterIP Interno (:5003)
                                                 • Namespace: apps (Acceso estrictamente interno)
                                                 • EF Core Code-First + OTel DB Spans
                                                        │
                                                        ▼
                                                 [ PostgreSQL 16 ]
                                                 • K8s ClusterIP (:5432) + PVC 10Gi
                                                 • Namespace: apps
                                                        │
         ┌──────────────────────────────────────────────┴──────────────────────────────────────────────┐
         │                                   Telemetría OTLP (:4317)                                   │
         ▼                                                                                            ▼
 [ OTel Collector ] ────────────────────► [ Jaeger UI :16686 ] (LoadBalancer Público + PVC 5Gi)
 • DaemonSet (1 agente por nodo GKE)     ├─────────────────────► [ Prometheus :9090 ] (LoadBalancer Público + PVC 10Gi)
 • Namespace: observability              └─────────────────────► [ Grafana :3000 ] (LoadBalancer Público + PVC 5Gi)
                                                                 • Datasources: Prometheus, Jaeger, Cloud Logging
                                                                 • Correlación Log ↔ Traza
 
 [ Chaos Mesh ] ───► Inyección de fallas: NetworkChaos (200ms latency), PodChaos (kill aleatorio), StressChaos (CPU)
```

---

## 🚀 Despliegue Local (Docker Compose)

### 1. Ejecutar Pruebas Unitarias
```bash
dotnet test
```

### 2. Construir y Levantar Contenedores Locales
```bash
docker compose up --build -d
```

### 3. URLs de Acceso Local
- **Passengers API**: `http://localhost:5001`
- **Checkin API**: `http://localhost:5002`
- **DataAccess API**: `http://localhost:5003`
- **Jaeger UI**: `http://localhost:16686`
- **Prometheus**: `http://localhost:9090`
- **Grafana UI**: `http://localhost:3000` (`admin` / `admin`)

---

## ☁️ Guía de Ejecución Paso a Paso en GCP (gcloud CLI & kubectl)

> **Importante para Toma de Evidencias**: Ejecuta los siguientes bloques en orden cronológico en tu consola / Cloud Shell y captura las evidencias solicitadas.

### 📌 Paso 0: Autenticación y Variables de Entorno
```bash
# 1. Autenticarse en Google Cloud
gcloud auth login

# 2. Configurar ID de Proyecto y Región
export PROJECT_ID="<TU_GCP_PROJECT_ID>"
export REGION="us-central1"
export CLUSTER_NAME="observability-lab-v2"
export SA_NAME="sa-observability"

gcloud config set project $PROJECT_ID
gcloud config set compute/region $REGION
```

---

### 📌 Paso 1: Habilitar APIs Requeridas en GCP
```bash
gcloud services enable \
    container.googleapis.com \
    sqladmin.googleapis.com \
    monitoring.googleapis.com \
    logging.googleapis.com \
    mesh.googleapis.com \
    securitycenter.googleapis.com \
    artifactregistry.googleapis.com
```

---

### 📌 Paso 2: Crear el Nuevo Clúster GKE Standard (`observability-lab-v2`)
*Se crea un clúster Standard con Workload Identity habilitado para permitir el despliegue del DaemonSet del OTel Collector y los controladores de Chaos Mesh.*

```bash
gcloud container clusters create $CLUSTER_NAME \
    --region=$REGION \
    --num-nodes=2 \
    --machine-type=e2-standard-4 \
    --enable-ip-alias \
    --workload-pool="${PROJECT_ID}.svc.id.goog" \
    --project=$PROJECT_ID
```
*(📸 **Evidencia**: Captura la salida de `gcloud container clusters list` mostrando el clúster `observability-lab-v2` en estado RUNNING).*

---

### 📌 Paso 3: Obtener Credenciales y Configurar IAM / Workload Identity
```bash
# 1. Obtener credenciales de kubectl para el nuevo clúster
gcloud container clusters get-credentials $CLUSTER_NAME --region=$REGION --project=$PROJECT_ID

# 2. Crear Service Account de GCP para observabilidad (si no existe)
gcloud iam service-accounts create $SA_NAME \
    --display-name="Observability Workload SA" \
    --project=$PROJECT_ID || true

SA_EMAIL="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

# 3. Asignar Roles de Observabilidad y Nube
gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="roles/monitoring.metricWriter"

gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="roles/cloudtrace.agent"

gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="roles/logging.logWriter"

gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="roles/logging.viewer"
```

---

### 📌 Paso 4: Instalar Chaos Mesh en GKE (para Chaos Engineering)
```bash
# 1. Crear namespace de Chaos Mesh
kubectl create namespace chaos-mesh

# 2. Instalar Chaos Mesh mediante Helm
helm repo add chaos-mesh https://charts.chaos-mesh.org
helm repo update
helm install chaos-mesh chaos-mesh/chaos-mesh \
    --namespace=chaos-mesh \
    --version 2.6.3 \
    --set chaosDaemon.runtime=containerd \
    --set chaosDaemon.socketPath=/run/containerd/containerd.sock

# 3. Verificar instalación
kubectl get pods -n chaos-mesh
```
*(📸 **Evidencia**: Captura la salida de `kubectl get pods -n chaos-mesh` con los pods `chaos-controller-manager` y `chaos-daemon` en estado Running).*

---

### 📌 Paso 5: Configurar Secretos en GitHub Actions y Desplegar
1. En GitHub, navega a **Settings** $\rightarrow$ **Secrets and variables** $\rightarrow$ **Actions**.
2. Asegúrate de tener configurados:
   - `GKE_CLUSTER_NAME_V2`: `observability-lab-v2`
   - `GKE_CLUSTER_REGION`: `us-central1`
   - `GCP_PROJECT_ID`: `<TU_PROJECT_ID>`
   - `GCP_WIF_PROVIDER`: `<TU_WIF_PROVIDER>`
   - `GCP_SERVICE_ACCOUNT`: `<TU_SERVICE_ACCOUNT_DEPLOY>`
   - `POSTGRES_USER`: `postgres`
   - `POSTGRES_PASSWORD`: `postgres`
   - `POSTGRES_DB`: `observability_db`
   - `GF_ADMIN_PASSWORD`: `admin`
3. Ejecuta los workflows de GitHub Actions en el siguiente orden:
   1. `Deploy Infrastructure Prerequisites to GKE` (`prerequisites.yml`)
   2. `Deploy Observability Stack to GKE` (`observability-gke.yml`)
   3. `DataAccess CI/CD` (`data-access.yml`)
   4. `Passengers CI/CD` (`passengers.yml`)
   5. `Checkin CI/CD` (`checkin.yml`)

*(📸 **Evidencia**: Captura el historial de GitHub Actions con los 5 workflows en verde).*

---

### 📌 Paso 6: Módulo A — Cloud SQL / Cloud Service Mesh (Observabilidad de Red L7)

#### A.1 Configurar Cloud SQL (Opcional si se utiliza PostgreSQL en GKE o Cloud SQL administrado):
```bash
# Crear instancia de Cloud SQL PostgreSQL (si se requiere instancia administrada)
gcloud sql instances create observability-cloudsql \
    --database-version=POSTGRES_16 \
    --tier=db-f1-micro \
    --region=$REGION \
    --root-password="postgres_secure_pass" \
    --project=$PROJECT_ID

gcloud sql databases create observability_db \
    --instance=observability-cloudsql \
    --project=$PROJECT_ID
```

#### A.2 Habilitar Google Cloud Service Mesh en el Clúster:
```bash
# Habilitar Service Mesh administrado en el clúster GKE
gcloud container fleet mesh enable --project=$PROJECT_ID

gcloud container fleet memberships register $CLUSTER_NAME-mem \
    --gke-cluster=${REGION}/${CLUSTER_NAME} \
    --enable-workload-identity \
    --project=$PROJECT_ID

gcloud container fleet mesh update \
    --management automatic \
    --memberships $CLUSTER_NAME-mem \
    --project=$PROJECT_ID
```
*(📸 **Evidencia**: Captura `gcloud container fleet mesh describe --project=$PROJECT_ID`).*

---

### 📌 Paso 7: Módulo B — AIOps (Detección de Anomalías en Cloud Monitoring y Prometheus)

#### B.1 Crear Política de Detección de Anomalías en Cloud Monitoring (gcloud CLI):
```bash
# Crear política de alerta con condición de anomalía en tasa de error
gcloud alpha monitoring policies create --policy-from-json='{
  "displayName": "AIOps - Anomaly Detection: High Error Rate in DataAccess",
  "combiner": "OR",
  "conditions": [
    {
      "displayName": "Error Rate > Baseline + 2sigma",
      "conditionThreshold": {
        "filter": "resource.type = \"k8s_container\" AND metric.type = \"custom.googleapis.com/dataaccess/errors/count\"",
        "comparison": "COMPARISON_GT",
        "thresholdValue": 2,
        "duration": "60s",
        "trigger": { "count": 1 },
        "aggregations": [
          {
            "alignmentPeriod": "60s",
            "perSeriesAligner": "ALIGN_RATE"
          }
        ]
      }
    }
  ]
}'
```

#### B.2 Verificar Regla AIOps en Prometheus:
1. Accede a la URL pública de Prometheus (`http://<PROMETHEUS_IP>:9090/alerts`).
2. Comprueba la regla `CorrelatedErrorRateAndLatencyAnomaly` (evalúa `error_rate > baseline + 2σ` AND `latency_p99 > 200ms`).

---

### 📌 Paso 8: Módulo C — Network Observability & Seguridad

#### C.1 Habilitar VPC Flow Logs en la subred de GKE:
```bash
gcloud compute networks subnets update default \
    --region=$REGION \
    --enable-flow-logs \
    --logging-aggregation-interval=INTERVAL_5_SEC \
    --logging-flow-sampling=0.5 \
    --logging-metadata=INCLUDE_ALL_METADATA
```
*(📸 **Evidencia**: Captura `gcloud compute networks subnets describe default --region=us-central1 | grep -A 5 logConfig`).*

#### C.2 Consultar Logs de Red en Cloud Logging:
```bash
gcloud logging read 'resource.type="gce_subnetwork" AND log_name=~"projects/'$PROJECT_ID'/logs/compute.googleapis.com%2Fvpc_flows"' \
    --limit=5 \
    --format="json"
```

---

### 📌 Paso 9: Módulo D — Pruebas de Chaos Engineering con Chaos Mesh

#### D.1 Aplicar Manifiestos de Experimentos de Caos:
```bash
kubectl apply -f k8s/gcp/11-chaos-experiments.yaml
```

#### D.2 Experimento 1: Inyección de Latencia de Red (200ms) en `Passengers`:
```bash
# Verificar estado del experimento
kubectl get networkchaos -n apps

# Enviar tráfico y medir latencia
PASSENGERS_IP=$(kubectl get svc passengers -n apps -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
for i in {1..20}; do
  curl -s -w "HTTP: %{http_code} | Tiempo: %{time_total}s\n" -o /dev/null "http://${PASSENGERS_IP}:5001/passengers/PAS-001"
  sleep 0.5
done
```
*(📸 **Evidencia**: Observa el tiempo de respuesta incrementado en 200ms y el span correspondiente en Jaeger UI).*

#### D.3 Experimento 2: Resiliencia de Pods (Pod Kill aleatorio con HPA 1 a 3 réplicas):
```bash
# Monitorear pods y autoscaling en tiempo real
kubectl get pods -n apps -w
```
*(📸 **Evidencia**: Captura los pods terminando y nuevos pods iniciando dentro de los límites de 1 a 3 réplicas).*

---

### 📌 Paso 10: Verificación de Spans en Jaeger y Dashboards en Grafana

1. **Jaeger UI (`http://<JAEGER_IP>:16686`)**:
   - Selecciona `checkin-service`.
   - Visualiza la traza completa distribuida de 17 spans con **OTel DB Semantic Conventions** (`db.system="postgresql"`, `db.statement`, `net.peer.name="postgres"`).
2. **Grafana UI (`http://<GRAFANA_IP>:3000`)**:
   - Inicia sesión con `admin` / `admin`.
   - Consulta el dashboard de Golden Signals y navega desde un log con error hacia su traza distribuida mediante el enlace de correlación (`Ver traza completa en Jaeger`).
