// Jenkinsfile for dotnet-demo-app
//
// Requirements on the Jenkins side:
//   - An agent with the docker CLI and access to a Docker daemon
//     (the 'jenkins' user must be able to run `docker ps`)
//   - Only standard plugins (Pipeline, Git). The MSTest plugin is optional:
//     if installed, test results show up in Jenkins; if not, they're just archived.
//
// The integration tests read their connection string from TodoApp.Tests/appsettings.Test.json
// (Server=localhost;Port=3306;Database=todo_test_db). To satisfy that without editing the
// repo, the .NET SDK container joins the network namespace of a throwaway MariaDB
// container, so "localhost:3306" inside the build container *is* the test database.
//
// Sources are copied into the build container with `docker cp` rather than bind-mounted,
// so this also works when Jenkins itself runs in a container that talks to the host's
// Docker daemon through a mounted socket.

pipeline {
    agent any

    options {
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
        disableConcurrentBuilds()
    }

    environment {
        DOTNET_IMAGE  = 'mcr.microsoft.com/dotnet/sdk:10.0'
        MARIADB_IMAGE = 'mariadb:11'

        // Demo credentials, identical to TodoApp.Tests/appsettings.Test.json.
        // For anything real, store these as Jenkins credentials instead.
        DB_NAME     = 'todo_test_db'
        DB_USER     = 'todo_usr'
        DB_PASSWORD = 'letmeinplz'
        DB_ROOT_PW  = 'sekrit'

        IMAGE_NAME  = 'todoapp'

        // Deployment: long-lived containers on a dedicated Docker network.
        // The app is published on host port 8081 (Jenkins already uses 8080).
        DEPLOY_NETWORK    = 'todoapp-net'
        DEPLOY_APP        = 'todoapp'
        DEPLOY_DB         = 'todoapp-db'
        DEPLOY_DB_VOLUME  = 'todoapp-db-data'
        DEPLOY_HOST_PORT  = '8081'
        APP_DB_NAME       = 'todo_db'
    }

    stages {
        stage('Prepare') {
            steps {
                script {
                    // Unique, docker-safe names for this build's throwaway containers
                    def tag = "${env.BUILD_TAG}".replaceAll('[^a-zA-Z0-9_.-]', '-')
                    env.DB_CONTAINER    = "todo-db-${tag}"
                    env.BUILD_CONTAINER = "todo-build-${tag}"
                }
                sh 'docker version'
            }
        }

        stage('Start test database') {
            steps {
                sh '''
                    docker run -d --name "$DB_CONTAINER" \
                        -e MARIADB_ROOT_PASSWORD="$DB_ROOT_PW" \
                        -e MARIADB_DATABASE="$DB_NAME" \
                        -e MARIADB_USER="$DB_USER" \
                        -e MARIADB_PASSWORD="$DB_PASSWORD" \
                        "$MARIADB_IMAGE"

                    echo "Waiting for MariaDB to become ready..."
                    for i in $(seq 1 30); do
                        if docker exec "$DB_CONTAINER" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1; then
                            echo "MariaDB is ready."
                            break
                        fi
                        if [ "$i" -eq 30 ]; then
                            echo "MariaDB did not start in time"; docker logs "$DB_CONTAINER"; exit 1
                        fi
                        sleep 2
                    done

                    # Create the todos table in the test database
                    docker exec -i "$DB_CONTAINER" \
                        mariadb -u"$DB_USER" -p"$DB_PASSWORD" "$DB_NAME" < TodoApp/schema.sql
                '''
            }
        }

        stage('Start build container') {
            steps {
                sh '''
                    docker run -d --name "$BUILD_CONTAINER" \
                        --network "container:$DB_CONTAINER" \
                        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
                        -e DOTNET_NOLOGO=1 \
                        -w /src \
                        "$DOTNET_IMAGE" sleep infinity

                    docker cp . "$BUILD_CONTAINER":/src
                '''
            }
        }

        stage('Restore') {
            steps {
                sh 'docker exec "$BUILD_CONTAINER" dotnet restore dotnet-demo.slnx'
            }
        }

        stage('Build') {
            steps {
                sh 'docker exec "$BUILD_CONTAINER" dotnet build dotnet-demo.slnx -c Release --no-restore'
            }
        }

        stage('Test') {
            steps {
                sh '''
                    docker exec "$BUILD_CONTAINER" dotnet test dotnet-demo.slnx -c Release --no-build \
                        --logger "trx;LogFileName=test-results.trx" \
                        --results-directory /src/TestResults
                '''
            }
            post {
                always {
                    sh 'rm -rf TestResults && docker cp "$BUILD_CONTAINER":/src/TestResults ./TestResults || true'
                    archiveArtifacts artifacts: 'TestResults/**/*.trx', allowEmptyArchive: true
                    script {
                        try {
                            mstest testResultsFile: 'TestResults/**/*.trx', keepLongStdio: true
                        } catch (NoSuchMethodError e) {
                            echo 'MSTest plugin not installed: test results archived but not shown as a trend.'
                        }
                    }
                }
            }
        }

        stage('Publish') {
            steps {
                sh '''
                    docker exec "$BUILD_CONTAINER" dotnet publish TodoApp/TodoApp.csproj -c Release --no-build -o /src/publish
                    docker exec "$BUILD_CONTAINER" tar czf /src/todoapp-$BUILD_NUMBER.tar.gz -C /src/publish .
                    docker cp "$BUILD_CONTAINER":/src/todoapp-$BUILD_NUMBER.tar.gz .
                '''
                archiveArtifacts artifacts: "todoapp-${BUILD_NUMBER}.tar.gz", fingerprint: true
            }
        }

        stage('Build Docker image') {
            steps {
                // Uses the repo's own multi-stage Dockerfile (build context = TodoApp/)
                sh 'docker build -t "$IMAGE_NAME:$BUILD_NUMBER" -t "$IMAGE_NAME:latest" TodoApp'
            }
        }

        stage('Deploy') {
            steps {
                sh '''
                    docker network inspect "$DEPLOY_NETWORK" >/dev/null 2>&1 \
                        || docker network create "$DEPLOY_NETWORK"

                    # Production-ish database: created once, data kept in a named volume
                    if ! docker inspect "$DEPLOY_DB" >/dev/null 2>&1; then
                        echo "Creating database container $DEPLOY_DB"
                        docker run -d --name "$DEPLOY_DB" \
                            --network "$DEPLOY_NETWORK" \
                            --restart unless-stopped \
                            -v "$DEPLOY_DB_VOLUME":/var/lib/mysql \
                            -e MARIADB_ROOT_PASSWORD="$DB_ROOT_PW" \
                            -e MARIADB_DATABASE="$APP_DB_NAME" \
                            -e MARIADB_USER="$DB_USER" \
                            -e MARIADB_PASSWORD="$DB_PASSWORD" \
                            "$MARIADB_IMAGE"
                    else
                        docker start "$DEPLOY_DB" >/dev/null
                    fi

                    echo "Waiting for $DEPLOY_DB..."
                    for i in $(seq 1 30); do
                        if docker exec "$DEPLOY_DB" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1; then
                            break
                        fi
                        if [ "$i" -eq 30 ]; then
                            echo "Database did not start in time"; docker logs "$DEPLOY_DB"; exit 1
                        fi
                        sleep 2
                    done

                    # schema.sql uses CREATE TABLE IF NOT EXISTS, so this is safe on every deploy
                    docker exec -i "$DEPLOY_DB" \
                        mariadb -u"$DB_USER" -p"$DB_PASSWORD" "$APP_DB_NAME" < TodoApp/schema.sql

                    # Replace the app container with the image from this build
                    docker rm -f "$DEPLOY_APP" >/dev/null 2>&1 || true
                    docker run -d --name "$DEPLOY_APP" \
                        --network "$DEPLOY_NETWORK" \
                        --restart unless-stopped \
                        -p "$DEPLOY_HOST_PORT":8080 \
                        -e ConnectionStrings__TodoDb="Server=$DEPLOY_DB;Port=3306;Database=$APP_DB_NAME;User=$DB_USER;Password=$DB_PASSWORD;" \
                        "$IMAGE_NAME:$BUILD_NUMBER"
                '''
            }
        }

        stage('Smoke test') {
            steps {
                // Checks the app over the Docker network, so it works whether Jenkins
                // runs on the host or in a container.
                sh '''
                    for i in $(seq 1 20); do
                        if docker run --rm --network "$DEPLOY_NETWORK" curlimages/curl:latest \
                                -fsS -o /dev/null "http://$DEPLOY_APP:8080/"; then
                            echo "App is up: http://<docker-host>:$DEPLOY_HOST_PORT/"
                            exit 0
                        fi
                        sleep 3
                    done
                    echo "App did not respond in time"
                    docker logs --tail 100 "$DEPLOY_APP"
                    exit 1
                '''
            }
        }
    }

    post {
        always {
            sh 'docker rm -f "$BUILD_CONTAINER" "$DB_CONTAINER" >/dev/null 2>&1 || true'
        }
        cleanup {
            deleteDir()
        }
    }
}
