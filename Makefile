.DEFAULT_GOAL := help

SOLUTION := Course.sln

help:  ## показать доступные команды
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
	 | awk -F':.*?## ' '{printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2}'

start_local_db:  ## поднять только postgres из compose (публикует 5432 на хост)
	docker compose up -d postgres

start_local_api:  ## запустить Course.Api локально против local db
	set -a && . ./.env && set +a && COURSE_DB_HOST=localhost dotnet run --project src/Course.Api

start_local_gateway:  ## запустить Course.Gateway локально
	set -a && . ./.env && set +a && COURSE_DB_HOST=localhost dotnet run --project src/Course.Gateway

start_local_cli:  ## запустить Course.Cli локально; ARGS="migration apply" передаёт команду
	set -a && . ./.env && set +a && COURSE_DB_HOST=localhost dotnet run --project src/Course.Cli -- $(ARGS)

format:  ## применить dotnet format
	dotnet format $(SOLUTION)

lint:  ## проверить форматирование и собрать с warnaserror
	dotnet format $(SOLUTION) --verify-no-changes
	dotnet build $(SOLUTION) -warnaserror

test:  ## прогнать тесты
	dotnet test $(SOLUTION)

ci: lint test  ## lint + test

start_prod:  ## поднять полный стек через compose
	docker compose up -d --build

stop_prod:  ## остановить полный стек
	docker compose down

check:  ## прогнать проверку задания
	./check.sh

e2e:  ## быстрый сквозной прогон обеих карт поверх уже собранного compose (ticket 14)
	python3 scripts/e2e_smoke.py

e2e_recovery:  ## прогнать все шесть week-4 failpoint recovery scenarios на чистом Compose
	python3 scripts/e2e_recovery.py

SERVICE ?= postgres
logs:  ## логи compose-стека, SERVICE=<имя> выбирает сервис
	docker compose logs -f $(SERVICE)

psql:  ## psql-сессия в контейнер postgres; ARGS="-c '...'" передаёт флаги psql
	docker compose exec postgres psql -U postgres -d course $(ARGS)

clean:  ## удалить bin/ и obj/
	find . -type d \( -name bin -o -name obj \) -not -path './.scratch/*' -prune -exec rm -rf {} +

.PHONY: help start_local_db start_local_api start_local_gateway start_local_cli format lint test ci start_prod stop_prod check e2e e2e_recovery logs psql clean
