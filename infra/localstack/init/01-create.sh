#!/bin/sh
set -eu
awslocal s3 mb s3://gerador-excel-local
awslocal sqs create-queue --queue-name gerador-excel-local
