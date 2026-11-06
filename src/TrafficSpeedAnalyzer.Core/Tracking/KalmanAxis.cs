namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>
/// Filtro di Kalman a velocità costante su una sola coordinata (posizione e velocità in unità/s).
/// Il tracker ne usa quattro indipendenti (centro x, centro y, larghezza, altezza): con matrici
/// diagonali a blocchi è equivalente al filtro a 8 stati di ByteTrack/SORT, ma più semplice.
/// Gestisce intervalli di tempo variabili (video a frame rate variabile, fotogrammi saltati).
/// </summary>
internal struct KalmanAxis
{
    private double _p00;
    private double _p01;
    private double _p11;

    public KalmanAxis(double position, double positionStd, double velocityStd)
    {
        Position = position;
        Velocity = 0;
        _p00 = positionStd * positionStd;
        _p01 = 0;
        _p11 = velocityStd * velocityStd;
    }

    public double Position { readonly get; private set; }

    public double Velocity { readonly get; private set; }

    public readonly double PositionVariance => _p00;

    /// <param name="dt">Intervallo in secondi.</param>
    /// <param name="accelerationStd">Deviazione standard dell'accelerazione (unità/s²), modello a rumore bianco.</param>
    public void Predict(double dt, double accelerationStd)
    {
        if (dt <= 0)
        {
            return;
        }

        Position += Velocity * dt;

        // P = F P Fᵀ + Q, con F = [[1, dt], [0, 1]] e Q del modello ad accelerazione casuale.
        double p00 = _p00 + dt * (2 * _p01 + dt * _p11);
        double p01 = _p01 + dt * _p11;
        double p11 = _p11;
        double q = accelerationStd * accelerationStd;
        double dt2 = dt * dt;
        _p00 = p00 + q * dt2 * dt2 / 4;
        _p01 = p01 + q * dt2 * dt / 2;
        _p11 = p11 + q * dt2;
    }

    public void Update(double measurement, double measurementStd)
    {
        double r = measurementStd * measurementStd;
        double s = _p00 + r;
        if (s <= 0)
        {
            return;
        }

        double k0 = _p00 / s;
        double k1 = _p01 / s;
        double innovation = measurement - Position;
        Position += k0 * innovation;
        Velocity += k1 * innovation;

        double p00 = (1 - k0) * _p00;
        double p01 = (1 - k0) * _p01;
        double p11 = _p11 - k1 * _p01;
        _p00 = p00;
        _p01 = p01;
        _p11 = p11;
    }
}
