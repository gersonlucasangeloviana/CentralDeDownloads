variable "aws_region" {
  description = "Região AWS em que os recursos serão criados."
  type        = string
}

variable "environment" {
  description = "Nome curto do ambiente, por exemplo dev ou prod."
  type        = string
  default     = "dev"

  validation {
    condition     = can(regex("^[a-z0-9-]{1,10}$", var.environment))
    error_message = "environment deve ter até 10 caracteres minúsculos, números ou hífen."
  }
}

variable "name_prefix" {
  description = "Prefixo dos recursos AWS."
  type        = string
  default     = "gerador-excel"

  validation {
    condition     = can(regex("^[a-z0-9-]{1,15}$", var.name_prefix))
    error_message = "name_prefix deve ter até 15 caracteres minúsculos, números ou hífen."
  }
}

variable "vpc_cidr" {
  description = "CIDR da VPC; escolha uma faixa sem sobreposição com a rede do Atlas."
  type        = string
  default     = "10.42.0.0/16"
}

variable "deploy_app" {
  description = "Ativa certificado, load balancer, DNS e os três serviços ECS depois do preparo inicial."
  type        = bool
  default     = false
}

variable "api_domain" {
  description = "Nome público da API, configurado no DNS da Cloudflare."
  type        = string
  default     = "apiga.vianadev.com.br"

  validation {
    condition     = length(trimspace(var.api_domain)) > 0
    error_message = "api_domain não pode ser vazio."
  }
}

variable "portal_domain" {
  description = "Nome público do portal, configurado no DNS da Cloudflare."
  type        = string
  default     = "ga.vianadev.com.br"

  validation {
    condition     = length(trimspace(var.portal_domain)) > 0 && var.portal_domain != var.api_domain
    error_message = "portal_domain deve ser diferente de api_domain e não pode ser vazio."
  }
}

variable "portal_allowed_cidrs" {
  description = "IPs públicos autorizados a abrir o portal, no formato CIDR."
  type        = list(string)
  default     = []

  validation {
    condition     = !var.deploy_app || length(var.portal_allowed_cidrs) > 0
    error_message = "Informe ao menos um IP em portal_allowed_cidrs quando deploy_app=true."
  }
}

variable "image_tag" {
  description = "Tag das três imagens no ECR; use uma tag nova por publicação."
  type        = string
  default     = ""

  validation {
    condition     = !var.deploy_app || length(trimspace(var.image_tag)) > 0
    error_message = "image_tag é obrigatória quando deploy_app=true."
  }
}

variable "cpu_architecture" {
  description = "Arquitetura das imagens Docker e das tasks Fargate."
  type        = string
  default     = "ARM64"

  validation {
    condition     = contains(["ARM64", "X86_64"], var.cpu_architecture)
    error_message = "cpu_architecture deve ser ARM64 ou X86_64."
  }
}

variable "mongo_database" {
  description = "Banco usado pela API e pelo worker no Atlas."
  type        = string
  default     = "gerador_excel"
}

variable "resend_secret_arn" {
  description = "ARN opcional de um segredo já preenchido com a API key do Resend."
  type        = string
  default     = null
}

variable "resend_from" {
  description = "Remetente opcional de e-mail, por exemplo arquivos@exemplo.com."
  type        = string
  default     = null
}

locals {
  name          = "${var.name_prefix}-${var.environment}"
  api_domain    = var.api_domain
  portal_domain = var.portal_domain
  availability_zones = slice(
    data.aws_availability_zones.available.names,
    0,
    2
  )
}
