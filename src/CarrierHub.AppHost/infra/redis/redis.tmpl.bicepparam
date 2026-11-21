using './redis-containerapp.module.bicep'

param env_outputs_azure_container_apps_environment_default_domain = '{{ .Env.ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN }}'
param env_outputs_azure_container_apps_environment_id = '{{ .Env.ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_ID }}'
param redis_password_value = '{{ securedParameter "redis_password" }}'
