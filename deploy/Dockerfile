FROM node:24-alpine AS web
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
ARG VERSION=0.0.0-dev
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Helpaffe.Domain/Helpaffe.Domain.csproj src/Helpaffe.Domain/
COPY src/Helpaffe.Application/Helpaffe.Application.csproj src/Helpaffe.Application/
COPY src/Helpaffe.Infrastructure/Helpaffe.Infrastructure.csproj src/Helpaffe.Infrastructure/
COPY src/Helpaffe.Api/Helpaffe.Api.csproj src/Helpaffe.Api/
RUN dotnet restore src/Helpaffe.Api/Helpaffe.Api.csproj
COPY src/Helpaffe.Domain/ src/Helpaffe.Domain/
COPY src/Helpaffe.Application/ src/Helpaffe.Application/
COPY src/Helpaffe.Infrastructure/ src/Helpaffe.Infrastructure/
COPY src/Helpaffe.Api/ src/Helpaffe.Api/
RUN dotnet publish src/Helpaffe.Api/Helpaffe.Api.csproj -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
RUN apk add --no-cache krb5-libs
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN chown -R $APP_UID:$APP_UID /app
USER $APP_UID
ENTRYPOINT ["./helpaffe"]
