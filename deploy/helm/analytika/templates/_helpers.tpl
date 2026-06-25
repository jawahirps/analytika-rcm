{{/* Expand the name of the chart. */}}
{{- define "analytika.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully qualified app name. */}}
{{- define "analytika.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "analytika.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Common labels. */}}
{{- define "analytika.labels" -}}
helm.sh/chart: {{ include "analytika.chart" . }}
app.kubernetes.io/name: {{ include "analytika.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Base selector labels. */}}
{{- define "analytika.selectorLabels" -}}
app.kubernetes.io/name: {{ include "analytika.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "analytika.web.selectorLabels" -}}
{{ include "analytika.selectorLabels" . }}
app.kubernetes.io/component: web
{{- end -}}

{{- define "analytika.worker.selectorLabels" -}}
{{ include "analytika.selectorLabels" . }}
app.kubernetes.io/component: worker
{{- end -}}

{{- define "analytika.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "analytika.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* Image reference (tag defaults to appVersion). */}}
{{- define "analytika.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}

{{/* Name of the secret holding DATABASE_URL. */}}
{{- define "analytika.databaseSecretName" -}}
{{- if .Values.database.existingSecret -}}
{{- .Values.database.existingSecret -}}
{{- else -}}
{{- printf "%s-db" (include "analytika.fullname" .) -}}
{{- end -}}
{{- end -}}

{{- define "analytika.databaseSecretKey" -}}
{{- if .Values.database.existingSecret -}}
{{- .Values.database.existingSecretKey -}}
{{- else -}}
DATABASE_URL
{{- end -}}
{{- end -}}

{{- define "analytika.sharedClaimName" -}}
{{- if .Values.sharedStorage.existingClaim -}}
{{- .Values.sharedStorage.existingClaim -}}
{{- else -}}
{{- printf "%s-shared" (include "analytika.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/* Shared volumeMounts (subPaths of the one RWX claim). Used by web + worker. */}}
{{- define "analytika.sharedVolumeMounts" -}}
- name: shared
  mountPath: {{ .Values.sharedStorage.mounts.data }}
  subPath: data
- name: shared
  mountPath: {{ .Values.sharedStorage.mounts.reports }}
  subPath: reports
- name: shared
  mountPath: {{ .Values.sharedStorage.mounts.downloads }}
  subPath: portal-downloads
{{- end -}}
