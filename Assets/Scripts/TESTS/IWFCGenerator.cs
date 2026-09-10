using System;

/// <summary>
/// Contrato común de los solvers WFC comparados
/// (MyWFC, GuminWFC, DeBroglieWFC). Permite que el arnés de benchmark
/// (<see cref="CalculateExecutionTime"/>) los trate de forma uniforme:
/// seleccionar uno por un único enum, leer sus dimensiones y suscribirse a
/// sus eventos de tiempo, sin bools por implementación ni acoplamiento cruzado.
///
/// Eventos (de instancia, no estáticos):
///   OnStart          → justo antes de comenzar a resolver (arranca el cronómetro)
///   OnEnd            → tras resolver con éxito, antes de instanciar (para el cronómetro)
///   OnIncompatibility → cada vez que un intento falla por contradicción
/// </summary>
public interface IWFCGenerator
{
    /// <summary>Genera un mapa completo (síncrono). Dispara OnStart/OnEnd/OnIncompatibility.</summary>
    void Generate();

    int DimensionsX { get; }
    int DimensionsY { get; }
    int DimensionsZ { get; }

    /// <summary>Etiqueta del algoritmo+config, usada como cabecera de columna en el CSV.</summary>
    string AlgorithmLabel { get; }

    /// <summary>Conjunto de tiles jugables (post-preprocesado), para las métricas de calidad.</summary>
    Tile[] TileObjects { get; }

    /// <summary>Tile resuelta en el índice lineal i = x + z*MX + y*MX*MZ. Null si no observada.</summary>
    Tile GetResolvedTile(int index);

    /// <summary>True si la tile es de infraestructura (no jugable): suelo, techo, límite…</summary>
    bool IsInfrastructureTile(Tile tile);

    event Action OnStart;
    event Action OnEnd;
    event Action OnIncompatibility;
}
