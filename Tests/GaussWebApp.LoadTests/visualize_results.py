import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns
import numpy as np
import os

plt.style.use('seaborn-v0_8-darkgrid')
sns.set_palette("husl")

def visualize_scalability(csv_file):
    """Визуализация масштабируемости"""
    df = pd.read_csv(csv_file)
    
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    
    # 1. Время выполнения
    ax = axes[0, 0]
    ax.plot(df['Threads'], df['Time(seconds)'], 'o-', linewidth=3, markersize=8)
    ax.set_xlabel('Количество потоков', fontsize=12)
    ax.set_ylabel('Время (секунды)', fontsize=12)
    ax.set_title('Время выполнения vs Количество потоков', fontsize=14, fontweight='bold')
    ax.grid(True, alpha=0.3)
    
    # Добавляем значения на график
    for i, row in df.iterrows():
        ax.annotate(f'{row["Time(seconds)"]:.3f}s', 
                   (row['Threads'], row['Time(seconds)']),
                   textcoords="offset points",
                   xytext=(0,10),
                   ha='center',
                   fontsize=10)
    
    # 2. Ускорение
    ax = axes[0, 1]
    ax.plot(df['Threads'], df['Speedup'], 'o-', linewidth=3, markersize=8, label='Фактическое')
    ax.plot(df['Threads'], df['Threads'], 'r--', linewidth=2, label='Линейное (идеальное)')
    ax.set_xlabel('Количество потоков', fontsize=12)
    ax.set_ylabel('Ускорение', fontsize=12)
    ax.set_title('Ускорение vs Количество потоков', fontsize=14, fontweight='bold')
    ax.legend()
    ax.grid(True, alpha=0.3)
    
    # 3. Эффективность
    ax = axes[1, 0]
    bars = ax.bar(df['Threads'], df['Efficiency(%)'], color='skyblue', alpha=0.8)
    ax.set_xlabel('Количество потоков', fontsize=12)
    ax.set_ylabel('Эффективность (%)', fontsize=12)
    ax.set_title('Эффективность параллелизации', fontsize=14, fontweight='bold')
    ax.set_ylim(0, 110)
    ax.grid(True, alpha=0.3, axis='y')
    
    # Добавляем значения на столбцы
    for bar in bars:
        height = bar.get_height()
        ax.annotate(f'{height:.1f}%',
                   xy=(bar.get_x() + bar.get_width() / 2, height),
                   xytext=(0, 3),
                   textcoords="offset points",
                   ha='center', va='bottom',
                   fontsize=10)
    
    # 4. Анализ эффективности
    ax = axes[1, 1]
    df['Theoretical_Speedup'] = df['Threads']
    df['Speedup_Loss'] = df['Theoretical_Speedup'] - df['Speedup']
    ax.bar(df['Threads'], df['Speedup_Loss'], color='lightcoral', alpha=0.8)
    ax.set_xlabel('Количество потоков', fontsize=12)
    ax.set_ylabel('Потеря ускорения', fontsize=12)
    ax.set_title('Потери ускорения (теоретическое - фактическое)', fontsize=14, fontweight='bold')
    ax.grid(True, alpha=0.3, axis='y')
    
    plt.tight_layout()
    plt.savefig('scalability_analysis.png', dpi=150, bbox_inches='tight')
    plt.show()
    
    # Вывод статистики
    print("\n" + "="*60)
    print("АНАЛИЗ МАСШТАБИРУЕМОСТИ")
    print("="*60)
    print(f"\nФайл данных: {csv_file}")
    print(f"\nЛучшее ускорение: {df['Speedup'].max():.2f}x при {df.loc[df['Speedup'].idxmax(), 'Threads']} потоках")
    print(f"Наилучшая эффективность: {df['Efficiency(%)'].max():.1f}% при {df.loc[df['Efficiency(%)'].idxmax(), 'Threads']} потоках")
    
    # Расчет среднего ускорения на поток
    df['Speedup_per_Thread'] = df['Speedup'] / df['Threads']
    print(f"Среднее ускорение на поток: {df['Speedup_per_Thread'].mean():.3f}x")

def visualize_memory_usage(csv_file):
    """Визуализация использования памяти"""
    df = pd.read_csv(csv_file)
    
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    
    # 1. Время выполнения
    ax = axes[0, 0]
    ax.plot(df['Размер'], df['Время(сек)'], 's-', linewidth=3, markersize=8)
    ax.set_xlabel('Размер матрицы (N×N)', fontsize=12)
    ax.set_ylabel('Время (секунды)', fontsize=12)
    ax.set_title('Время выполнения vs Размер матрицы', fontsize=14, fontweight='bold')
    ax.grid(True, alpha=0.3)
    
    # Логарифмическая шкала
    ax_log = axes[0, 1]
    ax_log.loglog(df['Размер'], df['Время(сек)'], 's-', linewidth=3, markersize=8)
    ax_log.set_xlabel('Размер матрицы (N×N)', fontsize=12)
    ax_log.set_ylabel('Время (секунды)', fontsize=12)
    ax_log.set_title('Логарифмический масштаб', fontsize=14, fontweight='bold')
    ax_log.grid(True, alpha=0.3)
    
    # Подгонка O(n³)
    from scipy.optimize import curve_fit
    def cubic_func(x, a):
        return a * x**3
    
    popt, _ = curve_fit(cubic_func, df['Размер'], df['Время(сек)'])
    x_fit = np.linspace(df['Размер'].min(), df['Размер'].max(), 100)
    y_fit = cubic_func(x_fit, *popt)
    ax_log.loglog(x_fit, y_fit, 'r--', label=f'O(n³): y = {popt[0]:.2e} * n³')
    ax_log.legend()
    
    # 2. Использование памяти
    ax = axes[1, 0]
    ax.plot(df['Размер'], df['Память(МБ)'], 'o-', linewidth=3, markersize=8, color='green')
    ax.set_xlabel('Размер матрицы (N×N)', fontsize=12)
    ax.set_ylabel('Память (МБ)', fontsize=12)
    ax.set_title('Использование памяти vs Размер матрицы', fontsize=14, fontweight='bold')
    ax.grid(True, alpha=0.3)
    
    # 3. Эффективность использования памяти
    ax = axes[1, 1]
    df['Memory_per_Cell_MB'] = df['Память(МБ)'] / (df['Размер'] ** 2)
    bars = ax.bar(range(len(df)), df['Memory_per_Cell_MB'] * 1024, color='orange', alpha=0.7)  
    ax.set_xlabel('Размер матрицы', fontsize=12)
    ax.set_ylabel('Память на ячейку (КБ)', fontsize=12)
    ax.set_title('Память на элемент матрицы', fontsize=14, fontweight='bold')
    ax.set_xticks(range(len(df)))
    ax.set_xticklabels(df['Размер'])
    ax.grid(True, alpha=0.3, axis='y')
    
    plt.tight_layout()
    plt.savefig('memory_analysis.png', dpi=150, bbox_inches='tight')
    plt.show()
    
    # Вывод статистики
    print("\n" + "="*60)
    print("АНАЛИЗ ИСПОЛЬЗОВАНИЯ ПАМЯТИ")
    print("="*60)
    
    # Расчет сложности
    df['Time_Ratio'] = df['Время(сек)'].pct_change() + 1
    df['Size_Ratio'] = df['Размер'].pct_change() + 1
    df['Complexity_Estimate'] = np.log(df['Time_Ratio']) / np.log(df['Size_Ratio'])
    
    print(f"\nОценка сложности алгоритма (по времени):")
    for i in range(1, len(df)):
        print(f"  {df['Размер'].iloc[i-1]} → {df['Размер'].iloc[i]}: O(n^{df['Complexity_Estimate'].iloc[i]:.2f})")
    
    print(f"\nСредняя память на элемент: {df['Memory_per_Cell_MB'].mean()*1024:.2f} КБ")
    print(f"Общий объем памяти для N=5000: {5000**2 * df['Memory_per_Cell_MB'].mean():.2f} МБ")

def main():
    """Основная функция"""
    print("="*60)
    print("ВИЗУАЛИЗАЦИЯ РЕЗУЛЬТАТОВ ТЕСТИРОВАНИЯ")
    print("="*60)
    
    # Проверяем наличие файлов
    files = {
        'scalability': 'scalability-results.csv',
        'memory': 'memory-usage.csv'
    }
    
    for name, path in files.items():
        if os.path.exists(path):
            print(f"✅ Найден файл {name}: {path}")
        else:
            print(f"❌ Файл {name} не найден: {path}")
            print(f"   Проверьте путь или запустите тесты для создания файлов")
            return
    
    try:
        # Визуализируем масштабируемость
        visualize_scalability(files['scalability'])
        
        # Визуализируем использование памяти
        visualize_memory_usage(files['memory'])
        
        print("\n" + "="*60)
        print("✅ Визуализация завершена успешно!")
        print("Созданные файлы:")
        print("  - scalability_analysis.png")
        print("  - memory_analysis.png")
        print("="*60)
        
    except Exception as e:
        print(f"❌ Ошибка при визуализации: {e}")
        print("Убедитесь, что установлены все зависимости:")
        print("  pip install pandas matplotlib seaborn numpy scipy")

if __name__ == "__main__":
    main()