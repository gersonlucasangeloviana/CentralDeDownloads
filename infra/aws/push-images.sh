#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Uso: ./push-images.sh TAG_UNICA" >&2
  exit 1
fi

tag="$1"
if [[ ! "$tag" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]]; then
  echo "Tag de imagem inválida." >&2
  exit 1
fi

cd "$(dirname "$0")"
region="$(terraform output -raw aws_region)"
api_repo="$(terraform output -json ecr_repositories | python3 -c 'import json,sys; print(json.load(sys.stdin)["api"])')"
worker_repo="$(terraform output -json ecr_repositories | python3 -c 'import json,sys; print(json.load(sys.stdin)["worker"])')"
portal_repo="$(terraform output -json ecr_repositories | python3 -c 'import json,sys; print(json.load(sys.stdin)["portal"])')"
registry="${api_repo%%/*}"

architecture="$(terraform console <<< 'var.cpu_architecture' | tr -d '"')"
case "$architecture" in
  ARM64) platform="linux/arm64" ;;
  X86_64) platform="linux/amd64" ;;
  *) echo "Arquitetura inválida: $architecture" >&2; exit 1 ;;
esac

aws ecr get-login-password --region "$region" | docker login --username AWS --password-stdin "$registry"

for service in api worker portal; do
  case "$service" in
    api) repo="$api_repo" ;;
    worker) repo="$worker_repo" ;;
    portal) repo="$portal_repo" ;;
  esac
  docker buildx build --platform "$platform" -f "../../Dockerfile.$service" -t "$repo:$tag" --push ../..
done

echo "Imagens publicadas com tag $tag. Defina image_tag = \"$tag\" em terraform.tfvars."
