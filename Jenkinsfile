// Jenkinsfile for dotnet-demo-app
//
// Needs only an agent with the docker CLI and access to a Docker daemon, plus the
// standard Pipeline and Git plugins. The MSTest plugin is optional (test trend in Jenkins).
//
// Tests: Dockerfile.tests builds an image with the solution compiled in. It runs on the
// network of a throwaway MariaDB, so "localhost:3306" (from appsettings.Test.json) is a
// fresh test database that's deleted after the build.
//
// Deploy: the app runs on host port 8081 (Jenkins uses 8080) next to a long-lived MariaDB
// whose data lives in a named volume. Both are created on the first deploy.

// Wait until a MariaDB container accepts connections, then load the schema into a database
def prepareDatabase(String container, String database) {
    sh """
        for i in \$(seq 1 30); do
            docker exec ${container} healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1 && break
            [ "\$i" -eq 30 ] && { docker logs ${container}; exit 1; }
            sleep 2
        done
        docker exec -i ${container} mariadb -u"\$DB_USER" -p"\$DB_PASSWORD" ${database} < TodoApp/schema.sql
    """
}

pipeline {
    agent any

    options {
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
        disableConcurrentBuilds()
    }

    environment {
        MARIADB_IMAGE = 'mariadb:11'

        DB_USER     = 'todo_usr'
        DB_PASSWORD = 'letmeinplz'
        DB_ROOT_PW  = 'sekrit'

        IMAGE      = "todoapp:${BUILD_NUMBER}"
        TEST_IMAGE = "todoapp-tests:${BUILD_NUMBER}"
        TEST_DB    = "todoapp-testdb-${BUILD_NUMBER}"
        TEST_RUN   = "todoapp-testrun-${BUILD_NUMBER}"
    }

    stages {
        stage('Build images') {
            steps {
                sh '''
                    docker build -t "$IMAGE" -t todoapp:latest ./TodoApp
                    docker build -f Dockerfile.tests -t "$TEST_IMAGE" .
                '''
            }
        }

        stage('Test') {
            steps {
                sh '''
                    docker run -d --name "$TEST_DB" \
                        -e MARIADB_ROOT_PASSWORD="$DB_ROOT_PW" \
                        -e MARIADB_DATABASE=todo_test_db \
                        -e MARIADB_USER="$DB_USER" \
                        -e MARIADB_PASSWORD="$DB_PASSWORD" \
                        "$MARIADB_IMAGE"
                '''
                prepareDatabase(env.TEST_DB, 'todo_test_db')
                sh 'docker run --name "$TEST_RUN" --network "container:$TEST_DB" "$TEST_IMAGE"'
            }
            post {
                always {
                    sh 'docker cp "$TEST_RUN":/src/TestResults ./TestResults || true'
                    archiveArtifacts artifacts: 'TestResults/**/*.trx', allowEmptyArchive: true
                    script {
                        try {
                            mstest testResultsFile: 'TestResults/**/*.trx', keepLongStdio: true
                        } catch (NoSuchMethodError e) {
                            echo 'MSTest plugin not installed: test results are archived only.'
                        }
                    }
                    sh '''
                        docker rm -f "$TEST_RUN" "$TEST_DB" >/dev/null 2>&1 || true
                        docker rmi "$TEST_IMAGE" >/dev/null 2>&1 || true
                    '''
                }
            }
        }

        stage('Archive build') {
            steps {
                // Take the published app out of the image, so every build keeps a download
                sh '''
                    docker create --name "todoapp-export-$BUILD_NUMBER" "$IMAGE" >/dev/null
                    docker cp "todoapp-export-$BUILD_NUMBER":/app ./publish
                    docker rm "todoapp-export-$BUILD_NUMBER" >/dev/null
                    tar czf "todoapp-$BUILD_NUMBER.tar.gz" -C publish .
                '''
                archiveArtifacts artifacts: "todoapp-${BUILD_NUMBER}.tar.gz", fingerprint: true
            }
        }

        stage('Deploy') {
            steps {
                sh '''
                    docker network inspect todoapp-net >/dev/null 2>&1 || docker network create todoapp-net

                    docker start todoapp-db >/dev/null 2>&1 || docker run -d --name todoapp-db \
                        --network todoapp-net --restart unless-stopped \
                        -v todoapp-db-data:/var/lib/mysql \
                        -e MARIADB_ROOT_PASSWORD="$DB_ROOT_PW" \
                        -e MARIADB_DATABASE=todo_db \
                        -e MARIADB_USER="$DB_USER" \
                        -e MARIADB_PASSWORD="$DB_PASSWORD" \
                        "$MARIADB_IMAGE"
                '''
                prepareDatabase('todoapp-db', 'todo_db')
                sh '''
                    docker rm -f todoapp >/dev/null 2>&1 || true
                    docker run -d --name todoapp \
                        --network todoapp-net --restart unless-stopped \
                        -p 8081:8080 \
                        -e ConnectionStrings__TodoDb="Server=todoapp-db;Port=3306;Database=todo_db;User=$DB_USER;Password=$DB_PASSWORD;" \
                        "$IMAGE"
                '''
            }
        }

        stage('Smoke test') {
            steps {
                sh '''
                    docker run --rm --network container:todoapp curlimages/curl:latest \
                        --fail --silent --show-error --output /dev/null \
                        --retry 15 --retry-connrefused --retry-delay 2 \
                        http://localhost:8080/ \
                    || { docker logs --tail 100 todoapp; exit 1; }
                    echo "App is up on port 8081"
                '''
            }
        }
    }

    post {
        cleanup {
            deleteDir()
        }
    }
}
