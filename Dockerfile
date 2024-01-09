# Use the .NET SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:7.0 as build

# Set build arguments
ARG TARGETPLATFORM
ARG TARGETARCH
ARG BUILDPLATFORM

# Display platform information
RUN echo "I am running on $BUILDPLATFORM, building for $TARGETPLATFORM"

# Set the working directory
WORKDIR /source

# Set environment variable for target architecture
ENV TARGETARCH=$TARGETARCH

# Copy the project file and restore dependencies
COPY worker/*.csproj .
COPY worker/.env .
RUN dotnet restore

# Copy the remaining source code
COPY . .

# Publish the application
RUN dotnet publish -c release -o /app --self-contained false --no-restore



# Switch to the runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0

# Set the working directory
WORKDIR /app
COPY worker/.env .

# Set environment variables
ENV DB_SERVER=db \
    DB_USERNAME=postgres \
    DB_PASSWORD=postgres \
    REDIS_HOSTNAME=redis \
    REDIS_HOST=redis

# Copy the published application from the build stage
COPY --from=build /app .

# Set the entry point for the application
ENTRYPOINT ["dotnet", "Worker.dll"]
