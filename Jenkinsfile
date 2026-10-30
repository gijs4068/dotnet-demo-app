// Jenkinsfile for https://github.com/HoGentTIN/dotnet-demo-app
//
// Requirements on the Jenkins side:
//   - An agent with Docker installed (the 'jenkins' user must be able to run docker)
//   - Plugins: Pipeline, Git, Docker Pipeline, MSTest (to show .trx test results)
//
// The integration tests read their connection string from TodoApp.Tests/appsettings.json
// (Server=localhost;Port=3306;Database=todo_test_db). To satisfy that without editing the
// repo, the .NET SDK container joins the network namespace of a throwaway MariaDB
// container, so "localhost:3306" inside the build container *is* the test database.
// No host port is published, so parallel builds on the same agent don't collide.

pipeline {
    agent any

    options {
        timestamps()
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
        stage('Checkout') {
            steps {
                checkout scm
                script {
                    // Unique, docker-safe name for this build's throwaway database
                    env.DB_CONTAINER = "todo-test-db-${env.BUILD_TAG}".replaceAll('[^a-zA-Z0-9_.-]', '-')
                }
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

        stage('Build & Test') {
            agent {
                docker {
                    image "${DOTNET_IMAGE}"
                    // Share the DB container's network: localhost:3306 -> MariaDB
                    args "--network container:${env.DB_CONTAINER}"
                    reuseNode true
                }
            }
            environment {
                // The SDK container runs as the Jenkins uid, which has no writable home
                HOME                        = "${WORKSPACE}"
                DOTNET_CLI_HOME             = "${WORKSPACE}/.dotnet"
                NUGET_PACKAGES              = "${WORKSPACE}/.nuget/packages"
                DOTNET_CLI_TELEMETRY_OPTOUT = '1'
                DOTNET_NOLOGO               = '1'
            }
            stages {
                stage('Restore') {
                    steps {
                        sh 'dotnet restore dotnet-demo.slnx'
                    }
                }
                stage('Build') {
                    steps {
                        sh 'dotnet build dotnet-demo.slnx -c Release --no-restore'
                    }
                }
                stage('Test') {
                    steps {
                        sh '''
                            dotnet test dotnet-demo.slnx -c Release --no-build \
                                --logger "trx;LogFileName=test-results.trx" \
                                --results-directory TestResults
                        '''
                    }
                    post {
                        always {
                            mstest testResultsFile: 'TestResults/**/*.trx', keepLongStdio: true
                        }
                    }
                }
                stage('Publish') {
                    steps {
                        sh 'dotnet publish TodoApp/TodoApp.csproj -c Release --no-build -o publish'
                        sh 'cd publish && tar czf ../todoapp-${BUILD_NUMBER}.tar.gz .'
                        archiveArtifacts artifacts: "todoapp-${BUILD_NUMBER}.tar.gz", fingerprint: true
                    }
                }
            }
        }

        stage('Build Docker image') {
            steps {
                // Uses the repo's own multi-stage Dockerfile (build context = TodoApp/)
                sh '''
                    docker build -t "$IMAGE_NAME:$BUILD_NUMBER" -t "$IMAGE_NAME:latest" TodoApp
                '''
            }
        }
    }

    post {
        always {
            sh 'docker rm -f "$DB_CONTAINER" || true'
            archiveArtifacts artifacts: 'TestResults/**/*', allowEmptyArchive: true
        }
        cleanup {
            cleanWs()
        }
    }
}
