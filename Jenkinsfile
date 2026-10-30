// Jenkinsfile for dotnet-demo-app
//
// Requirements on the Jenkins side:
//   - An agent with the docker CLI and access to a Docker daemon
//     (the 'jenkins' user must be able to run `docker ps`)
//   - Only standard plugins (Pipeline, Git). The MSTest plugin is optional:
//     if installed, test results show up in Jenkins; if not, they're just archived.
//
// The integration tests read their connection string from TodoApp.Tests/appsettings.json
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

        // Demo credentials, identical to TodoApp.Tests/appsettings.json.
        // For anything real, store these as Jenkins credentials instead.
        DB_NAME     = 'todo_test_db'
        DB_USER     = 'todo_usr'
        DB_PASSWORD = 'letmeinplz'
        DB_ROOT_PW  = 'sekrit'

        IMAGE_NAME  = 'todoapp'
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
