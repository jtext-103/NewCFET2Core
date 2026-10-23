#!/usr/bin/env bash
case "$1" in
  *Username*) printf '%s\n' "$CI_PUSH_USER" ;;
  *Password*) printf '%s\n' "$CI_PUSH_TOKEN" ;;
  *) exit 1 ;;
esac
