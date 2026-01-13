set -e  

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' 

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

    print_info "Проверка структуры проекта..."
    if [ ! -d "Tests" ]; then
        print_error "Папка Tests не найдена!"
        exit 1
    fi

    print_info "Создание nodes.txt для тестов..."
    echo "worker:9001" > GaussWebApp/nodes.txt
    echo "worker:9002" >> GaussWebApp/nodes.txt
    echo "worker:9003" >> GaussWebApp/nodes.txt
    
    print_info "=== ЗАПУСК UNIT-ТЕСТОВ ==="
    local unit_start=$(date +%s)
    
    if dotnet test Tests/GaussWebApp.UnitTests/GaussWebApp.UnitTests.csproj \
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
    
    local end_time=$(date +%s)
    local total_duration=$((end_time - start_time))
    
    print_info "=== ИТОГИ ТЕСТИРОВАНИЯ ==="
    print_info "Общее время выполнения: ${total_duration} секунд"
    print_info "Время завершения: $(date)"
}

run_tests

print_success "=== ТЕСТИРОВАНИЕ ЗАВЕРШЕНО ==="
exit 0