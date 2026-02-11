# Refer to https://hub.docker.com/_/microsoft-dotnet-sdk for available versions
FROM mcr.microsoft.com/dotnet/sdk:10.0.103@sha256:0a506ab0c8aa077361af42f82569d364ab1b8741e967955d883e3f23683d473a

# Installing mono makes `dotnet test` work without errors even for net472.
# But installing it takes a long time, so it's excluded by default.
#RUN apt-key adv --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys 3FA7E0328081BFF6A14DA29AA6A19B38D3D831EF
#RUN echo "deb https://download.mono-project.com/repo/ubuntu stable-bionic main" | tee /etc/apt/sources.list.d/mono-official-stable.list
#RUN apt-get update
#RUN DEBIAN_FRONTEND=noninteractive apt-get install -y mono-devel

# Clear the NUGET_XMLDOC_MODE env var so xml api doc files get unpacked, allowing a rich experience in Intellisense.
# See https://github.com/dotnet/dotnet-docker/issues/2790 for a discussion on this, where the prioritized use case
# was *not* devcontainers, sadly.
ENV NUGET_XMLDOC_MODE=
