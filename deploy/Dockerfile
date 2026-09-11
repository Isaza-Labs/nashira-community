# syntax=docker/dockerfile:1.7

# nashira backend — API only. The SvelteKit frontend is its own service/image
# (see deploy/Dockerfile.frontend + the `frontend` service in docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nashira_backend/nashira_backend.csproj nashira_backend/
RUN dotnet restore nashira_backend/nashira_backend.csproj --runtime linux-x64

COPY nashira_backend/ nashira_backend/
RUN dotnet publish nashira_backend/nashira_backend.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Hybrid .NET + Python image. Python backs the multi-vendor SSH runner (netmiko);
# see deploy/python/README.md. iputils-ping backs device reachability checks
# (System.Net.NetworkInformation.Ping shells out to /usr/bin/ping when the
# process lacks CAP_NET_RAW).
#
# fonts-dejavu-core is for PDF exports. QuestPDF bundles Lato and its own native
# renderer, so a PDF builds without it — but with no monospace font on the image the
# code blocks in a report fall back to a proportional face, and pasted CLI output
# loses the column alignment that made it worth pasting. ~1.5 MB for that.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-pip iputils-ping fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*

# netmiko/paramiko back the SSH runner. Pinned to netmiko 4.x.
RUN pip3 install --no-cache-dir --break-system-packages --ignore-installed \
      "netmiko>=4,<5" "paramiko>=3" "textfsm>=1.1" "ntc-templates>=4"

# SSH runner + helpers (nashira_ssh_runner.py + parsers/clean + ntc_templates_extra),
# lifted from flow-weaver. Imported as sibling modules via PYTHONPATH.
COPY deploy/python/ /usr/local/lib/nashira_python/
ENV PYTHONPATH=/usr/local/lib/nashira_python

# Every directory a named volume mounts over has to exist in the IMAGE and be owned
# by `app`. Docker seeds a fresh volume from whatever is at the mount point, and
# creates the mount point as root when nothing is there — leaving a root-owned volume
# the non-root process cannot write to.
#
#   /app/keyring    data-protection keys
#   /app/pyenv/site pip packages the provisioner installs
#   /app/data/git   repository checkouts (Git:Root = ./data/git, relative to /app)
#
# This was written for pyenv and git was left out of it, so every clone failed with a
# permission error and the git screen had never worked.
RUN mkdir -p /app/keyring /app/pyenv/site /app/data/git && chown -R app:app /app
COPY --from=build --chown=app:app /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeyRingPath=/app/keyring \
    Python__PackagesDir=/app/pyenv \
    DOTNET_RUNNING_IN_CONTAINER=true

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "nashira_backend.dll"]
