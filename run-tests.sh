#!/bin/bash

# run-tests.sh - Скрипт для запуска тестов с подробной информацией

set -e  # Выход при первой ошибке

# Цвета для вывода
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Функции для вывода
print_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Основная функция
run_tests() {
    local start_time=$(date +%s)
    
    print_info "=== ЗАПУСК ТЕСТОВ GAUSS SYSTEM ==="
    print_info "Время начала: $(date)"
    print_info "Текущая директория: $(pwd)"
    
    # Проверка структуры проекта
    print_info "Проверка структуры проекта..."
    if [ ! -d "Tests" ]; then
        print_error "Папка Tests не найдена!"
        exit 1
    fi
    
    # Создание папок для результатов
    print_info "Создание папок для результатов..."
    mkdir -p test-results/unit
    mkdir -p test-results/load
    mkdir -p test-results/coverage
    
    # Создание nodes.txt для тестов
    print_info "Создание nodes.txt для тестов..."
    echo "worker:9001" > GaussWebApp/nodes.txt
    echo "worker:9002" >> GaussWebApp/nodes.txt
    echo "worker:9003" >> GaussWebApp/nodes.txt
    
    # Запуск тестов
    print_info "=== ЗАПУСК UNIT-ТЕСТОВ ==="
    local unit_start=$(date +%s)
    
    if dotnet test Tests/GaussWebApp.UnitTests/GaussWebApp.UnitTests.csproj \
        --logger "trx;LogFileName=unit-tests.trx" \
        --results-directory test-results/unit \
        --verbosity normal; then
        local unit_end=$(date +%s)
        local unit_duration=$((unit_end - unit_start))
        print_success "Unit-тесты завершены успешно за ${unit_duration} секунд"
    else
        print_error "Unit-тесты завершились с ошибкой"
        local unit_end=$(date +%s)
        local unit_duration=$((unit_end - unit_start))
        print_warning "Время выполнения unit-тестов: ${unit_duration} секунд"
    fi
    
    print_info "=== ЗАПУСК LOAD-ТЕСТОВ ==="
    local load_start=$(date +%s)
    
    if dotnet test Tests/GaussWebApp.LoadTests/GaussWebApp.LoadTests.csproj \
        --logger "trx;LogFileName=load-tests.trx" \
        --results-directory test-results/load \
        --verbosity normal; then
        local load_end=$(date +%s)
        local load_duration=$((load_end - load_start))
        print_success "Load-тесты завершены успешно за ${load_duration} секунд"
    else
        print_error "Load-тесты завершились с ошибкой"
        local load_end=$(date +%s)
        local load_duration=$((load_end - load_start))
        print_warning "Время выполнения load-тестов: ${load_duration} секунд"
    fi
    
    # Анализ результатов
    analyze_results
    
    # Итоговая статистика
    local end_time=$(date +%s)
    local total_duration=$((end_time - start_time))
    
    print_info "=== ИТОГИ ТЕСТИРОВАНИЯ ==="
    print_info "Общее время выполнения: ${total_duration} секунд"
    print_info "Время завершения: $(date)"
    print_info "Результаты сохранены в папке test-results/"
    
    # Показ содержимого папки с результатами
    print_info "Содержимое папки test-results/:"
    find test-results -type f -name "*.trx" | while read file; do
        print_info "  - $(basename "$file")"
    done
}

# Функция анализа результатов
analyze_results() {
    print_info "=== АНАЛИЗ РЕЗУЛЬТАТОВ ==="
    
    # Проверка файлов результатов
    local unit_result=$(find test-results/unit -name "*.trx" -type f | head -1)
    local load_result=$(find test-results/load -name "*.trx" -type f | head -1)
    
    if [ -n "$unit_result" ]; then
        print_info "Unit-тесты: результаты в $unit_result"
        
        # Простой анализ TRX файла (можно добавить более сложный парсинг)
        local unit_tests=$(grep -c "UnitTestResult" "$unit_result" 2>/dev/null || echo "0")
        local unit_passed=$(grep -c 'outcome="Passed"' "$unit_result" 2>/dev/null || echo "0")
        local unit_failed=$(grep -c 'outcome="Failed"' "$unit_result" 2>/dev/null || echo "0")
        
        print_info "  Всего тестов: $unit_tests"
        print_info "  Успешно: $unit_passed"
        print_info "  Провалено: $unit_failed"
        
        if [ "$unit_failed" -eq "0" ]; then
            print_success "  Все unit-тесты прошли успешно!"
        else
            print_warning "  Есть проваленные unit-тесты: $unit_failed"
        fi
    else
        print_warning "Файл результатов unit-тестов не найден"
    fi
    
    if [ -n "$load_result" ]; then
        print_info "Load-тесты: результаты в $load_result"
        
        local load_tests=$(grep -c "UnitTestResult" "$load_result" 2>/dev/null || echo "0")
        local load_passed=$(grep -c 'outcome="Passed"' "$load_result" 2>/dev/null || echo "0")
        local load_failed=$(grep -c 'outcome="Failed"' "$load_result" 2>/dev/null || echo "0")
        
        print_info "  Всего тестов: $load_tests"
        print_info "  Успешно: $load_passed"
        print_info "  Провалено: $load_failed"
    else
        print_warning "Файл результатов load-тестов не найден"
    fi
    
}


# Основной вызов
run_tests

# Опционально: генерация coverage отчета
# generate_coverage

print_success "=== ТЕСТИРОВАНИЕ ЗАВЕРШЕНО ==="
exit 0