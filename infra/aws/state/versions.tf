terraform {
  required_version = ">= 1.10.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "aws_region" {
  type        = string
  description = "Região do bucket de estado."
}

variable "environment" {
  type        = string
  description = "Ambiente, por exemplo dev ou prod."
}

provider "aws" {
  region = var.aws_region
}
