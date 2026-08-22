.DEFAULT_GOAL := help

SOLUTION := Course.sln

help:  ## показать доступные команды
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
	 | awk -F':.*?## ' '{printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2}'

start_local_db:  ## поднять только postgres из compose (появится в тикете 02)
	@echo "start_local_db: compose появится в тикете 02"

start_local_api:  ## запустить Course.Api локально против local db
	dotnet run --project src/Course.Api

start_local_gateway:  ## запустить Course.Gateway локально
	dotnet run --project src/Course.Gateway

start_local_cli:  ## запустить Course.Cli локально
	dotnet run --project src/Course.Cli

format:  ## применить dotnet format
	dotnet format $(SOLUTION)

lint:  ## проверить форматирование и собрать с warnaserror
	dotnet format $(SOLUTION) --verify-no-changes
	dotnet build $(SOLUTION) -warnaserror

test:  ## прогнать тесты
	dotnet test $(SOLUTION)

ci: lint test  ## lint + test

start_prod:  ## поднять полный стек через compose (появится в тикете 02)
	@echo "start_prod: compose появится в тикете 02"

stop_prod:  ## остановить полный стек
	@echo "stop_prod: compose появится в тикете 02"

check:  ## прогнать проверку задания
	./check.sh

logs:  ## логи compose-стека
	@echo "logs: compose появится в тикете 02"

psql:  ## psql-сессия в контейнер postgres
	@echo "psql: compose появится в тикете 02"

clean:  ## удалить bin/ и obj/
	find . -type d \( -name bin -o -name obj \) -not -path './.scratch/*' -prune -exec rm -rf {} +

.PHONY: help start_local_db start_local_api start_local_gateway start_local_cli format lint test ci start_prod stop_prod check logs psql clean
