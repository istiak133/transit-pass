# One Dockerfile for every TransitPass process. Compose passes which project to build.
#   docker build --build-arg PROJECT=src/Gateway/TransitPass.Gateway/TransitPass.Gateway.csproj \
#                --build-arg APP_DLL=TransitPass.Gateway.dll .

# ---- build stage: full SDK, thrown away after publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
# The image ships its own pinned SDK, so the repo's global.json pin (for developer machines and CI) is not needed here.
RUN rm -f global.json \
 && dotnet restore "$PROJECT" --locked-mode \
 && dotnet publish "$PROJECT" -c Release -o /app --no-restore -p:UseAppHost=false

# ---- runtime stage: ASP.NET runtime only, no SDK or source code ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG APP_DLL
ENV APP_DLL=${APP_DLL} \
    ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
# The base image defines a non-root "app" user. A compromised process cannot write to the image.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
