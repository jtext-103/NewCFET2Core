#!/usr/bin/env bash
set -euo pipefail
: "${NUGET_API_KEY:?Configure NUGET_API_KEY in GitLab CI/CD variables}"
: "${CI_PUSH_USER:?Configure CI_PUSH_USER in GitLab CI/CD variables}"
: "${CI_PUSH_TOKEN:?Configure CI_PUSH_TOKEN in GitLab CI/CD variables}"
mkdir -p artifacts
git fetch origin "$CI_DEFAULT_BRANCH"
remote_sha=$(git rev-parse "origin/$CI_DEFAULT_BRANCH")
marker="NuGet release for $CI_COMMIT_SHA"
reserved=false
if [ "$remote_sha" != "$CI_COMMIT_SHA" ]; then
  # A retry can reuse only the version-only child commit reserved by this pipeline.
  if [ "$(git log -1 --format=%P "$remote_sha")" = "$CI_COMMIT_SHA" ] &&
     git log -1 --format=%B "$remote_sha" | grep -Fq "$marker" &&
     [ "$(git diff --name-only "$CI_COMMIT_SHA" "$remote_sha")" = "$PACKAGE_PROJECT" ]; then
    git checkout --detach "$remote_sha"
    reserved=true
  else
    echo "Default branch advanced; no package will be published from this stale pipeline."
    exit 1
  fi
fi
old_version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$PACKAGE_PROJECT")
[[ "$old_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Expected one a.b.c Version'; exit 1; }
version=$old_version
if [ "$reserved" = false ]; then
  IFS=. read -r major minor patch <<< "$old_version"
  if printf '%s\n' "$CI_COMMIT_MESSAGE" | grep -qE -- '-a[[:space:]]*$'; then
    version="$((major + 1)).0.0"
  elif printf '%s\n' "$CI_COMMIT_MESSAGE" | grep -qE -- '-b[[:space:]]*$'; then
    version="$major.$((minor + 1)).0"
  else
    version="$major.$minor.$((patch + 1))"
  fi
  sed -i "s/<Version>$old_version<\/Version>/<Version>$version<\/Version>/" "$PACKAGE_PROJECT"
fi
echo "Publishing $PACKAGE_ID $version from $CI_COMMIT_SHA"
dotnet test "$TEST_PROJECT" -c Release --logger 'trx;LogFileName=tests.trx' --results-directory "$CI_PROJECT_DIR/artifacts/tests"
dotnet pack "$PACKAGE_PROJECT" -c Release --no-build -o "$CI_PROJECT_DIR/artifacts/packages" -p:RepositoryCommit="$CI_COMMIT_SHA"
package="$CI_PROJECT_DIR/artifacts/packages/$PACKAGE_ID.$version.nupkg"
test -f "$package"
if [ "$reserved" = false ]; then
  git config user.name 'CI Bot'
  git config user.email 'ci@example.com'
  git add -- "$PACKAGE_PROJECT"
  git commit -m "$marker; $old_version -> $version [skip ci]"
  export GIT_ASKPASS="$CI_PROJECT_DIR/.ci/git-askpass.sh"
  export GIT_TERMINAL_PROMPT=0
  chmod 700 "$GIT_ASKPASS"
  # Ordinary fast-forward push: fail on conflicts, never rebase or force push.
  git -c credential.helper= push "$CI_PROJECT_URL.git" "HEAD:refs/heads/$CI_DEFAULT_BRANCH"
fi
{
  printf 'PACKAGE_ID=%s\nPACKAGE_VERSION=%s\nSOURCE_COMMIT=%s\nVERSION_COMMIT=%s\n' "$PACKAGE_ID" "$version" "$CI_COMMIT_SHA" "$(git rev-parse HEAD)"
  sha256sum "$package"
} > artifacts/release.txt
dotnet nuget push "$package" --source "$NUGET_SOURCE" --api-key "$NUGET_API_KEY"
# Verify through a new consumer and isolated cache rather than trusting upload success.
probe=$(mktemp -d)
trap 'rm -rf "$probe"' EXIT
cat > "$probe/Probe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="$PACKAGE_ID" Version="[$version]" /></ItemGroup></Project>
EOF
for attempt in $(seq 1 20); do
  if dotnet restore "$probe/Probe.csproj" --source "$NUGET_SOURCE" --packages "$probe/packages" --no-http-cache; then
    echo "Package restore verified: $PACKAGE_ID $version"
    exit 0
  fi
  sleep 10
done
echo 'Package availability verification timed out; do not update downstream dependencies.'
exit 1
