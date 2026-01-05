"""
🌙 Moon Dev's Data Loader Utility
Unified data loading from PostgreSQL or CSV files for backtesting.

Usage:
    from src.utils.data_loader import load_ohlcv

    # Load from PostgreSQL (if DATABASE_URL is set)
    data = load_ohlcv('ES', '15m')

    # Load from CSV fallback
    data = load_ohlcv('BTC-USD', '15m')
"""

import os
import pandas as pd
from pathlib import Path
from dotenv import load_dotenv

load_dotenv()

# Database connection (lazy loaded)
_engine = None

def get_engine():
    """Get or create SQLAlchemy engine for PostgreSQL connection."""
    global _engine
    if _engine is None:
        database_url = os.getenv('DATABASE_URL')
        if database_url:
            from sqlalchemy import create_engine
            _engine = create_engine(database_url)
    return _engine


def load_ohlcv(symbol: str, timeframe: str = '15m', days_back: int = None,
               table_name: str = 'ohlcv_data') -> pd.DataFrame:
    """
    🌙 Moon Dev's Universal OHLCV Loader

    Loads OHLCV data from PostgreSQL if DATABASE_URL is configured,
    otherwise falls back to CSV files.

    Supports:
    - Futures: ES, NQ, GC, CL
    - Crypto: BTC-USD, ETH-USD, SOL-USD
    - Stocks: SPY, AAPL, TSLA, etc. (from databento)

    Args:
        symbol: Trading symbol (e.g., 'ES', 'NQ', 'GC', 'CL', 'BTC-USD', 'SPY')
        timeframe: Data timeframe (e.g., '15m', '1H', '4H', '1D')
        days_back: Optional limit on how many days of data to load
        table_name: PostgreSQL table name (default: 'ohlcv_data')

    Returns:
        pd.DataFrame with columns: Open, High, Low, Close, Volume
        Index: datetime

    PostgreSQL Table Schema Expected:
        CREATE TABLE ohlcv_data (
            id SERIAL PRIMARY KEY,
            symbol VARCHAR(20) NOT NULL,
            timeframe VARCHAR(10) NOT NULL,
            datetime TIMESTAMP NOT NULL,
            open NUMERIC NOT NULL,
            high NUMERIC NOT NULL,
            low NUMERIC NOT NULL,
            close NUMERIC NOT NULL,
            volume NUMERIC NOT NULL,
            UNIQUE(symbol, timeframe, datetime)
        );
        CREATE INDEX idx_ohlcv_symbol_tf ON ohlcv_data(symbol, timeframe);
    """
    engine = get_engine()

    if engine is not None:
        # Load from PostgreSQL
        return _load_from_postgres(engine, symbol, timeframe, days_back, table_name)
    else:
        # Fallback to CSV
        return load_ohlcv_from_csv(symbol, timeframe)


def _load_from_postgres(engine, symbol: str, timeframe: str,
                        days_back: int = None, table_name: str = 'ohlcv_data') -> pd.DataFrame:
    """Load OHLCV data from PostgreSQL database."""
    from sqlalchemy import text

    # Build query
    query = f"""
        SELECT datetime, open, high, low, close, volume
        FROM {table_name}
        WHERE symbol = :symbol
          AND timeframe = :timeframe
    """

    if days_back:
        query += f" AND datetime >= NOW() - INTERVAL '{days_back} days'"

    query += " ORDER BY datetime ASC"

    # Execute query
    with engine.connect() as conn:
        data = pd.read_sql(
            text(query),
            conn,
            params={'symbol': symbol, 'timeframe': timeframe}
        )

    if data.empty:
        raise ValueError(f"🌙 No data found for {symbol} {timeframe} in PostgreSQL")

    # Format for backtesting.py
    data['datetime'] = pd.to_datetime(data['datetime'])
    data = data.set_index('datetime')
    data.columns = ['Open', 'High', 'Low', 'Close', 'Volume']

    # Ensure numeric types
    for col in data.columns:
        data[col] = pd.to_numeric(data[col], errors='coerce')

    print(f"🌙 Loaded {len(data)} rows for {symbol} {timeframe} from PostgreSQL")
    return data


def load_ohlcv_from_csv(symbol: str, timeframe: str = '15m') -> pd.DataFrame:
    """
    Load OHLCV data from CSV file (fallback when no database).

    Looks for files in: src/data/rbi/{symbol}-{timeframe}.csv
    """
    # Find project root
    project_root = Path(__file__).parent.parent.parent

    # Try different filename patterns
    patterns = [
        f"{symbol}-{timeframe}.csv",
        f"{symbol}_{timeframe}.csv",
        f"{symbol}.csv",
    ]

    data_dir = project_root / "src" / "data" / "rbi"

    for pattern in patterns:
        csv_path = data_dir / pattern
        if csv_path.exists():
            data = pd.read_csv(csv_path)

            # Clean column names
            data.columns = data.columns.str.strip().str.lower()

            # Drop unnamed columns
            data = data.drop(columns=[col for col in data.columns if 'unnamed' in col.lower()], errors='ignore')

            # Set datetime index
            if 'datetime' in data.columns:
                data['datetime'] = pd.to_datetime(data['datetime'])
                data = data.set_index('datetime')

            # Rename to backtesting.py format
            data = data.rename(columns={
                'open': 'Open',
                'high': 'High',
                'low': 'Low',
                'close': 'Close',
                'volume': 'Volume'
            })

            # Keep only required columns
            data = data[['Open', 'High', 'Low', 'Close', 'Volume']]

            print(f"🌙 Loaded {len(data)} rows for {symbol} from CSV: {csv_path}")
            return data

    raise FileNotFoundError(
        f"🌙 No CSV found for {symbol}. "
        f"Expected files in {data_dir}: {patterns}"
    )


def get_available_symbols(table_name: str = 'ohlcv_data') -> list:
    """
    Get list of available symbols from PostgreSQL database.

    Returns:
        List of (symbol, timeframe) tuples available in the database.
    """
    engine = get_engine()

    if engine is None:
        # Return available CSV files instead
        project_root = Path(__file__).parent.parent.parent
        data_dir = project_root / "src" / "data" / "rbi"

        if data_dir.exists():
            csv_files = list(data_dir.glob("*.csv"))
            return [f.stem for f in csv_files]
        return []

    from sqlalchemy import text

    query = f"SELECT DISTINCT symbol, timeframe FROM {table_name} ORDER BY symbol, timeframe"

    with engine.connect() as conn:
        result = pd.read_sql(text(query), conn)

    return list(result.itertuples(index=False, name=None))


# Convenience function for the RBI agent
def load_futures_data(symbol: str, timeframe: str = '15m') -> pd.DataFrame:
    """
    🌙 Convenience wrapper for loading futures data.
    Alias for load_ohlcv() with clearer naming for futures traders.
    """
    return load_ohlcv(symbol, timeframe)


# ============================================
# 🌙 DATABENTO DATA LOADER - Moon Dev
# ============================================

# Databento data directories
DATABENTO_SPY_DIR = Path("C:/dev/databento/XNAS-20250520-MPSSD5E4CR")
DATABENTO_ES_DIR = Path("C:/dev/databento/GLBX-20251122-DHFVWN9D6Q")

# Default data directory (SPY)
DATABENTO_DATA_DIR = DATABENTO_SPY_DIR

# Symbol to directory mapping
DATABENTO_SYMBOL_DIRS = {
    'SPY': DATABENTO_SPY_DIR,
    'ES': DATABENTO_ES_DIR,
    'NQ': DATABENTO_ES_DIR,  # If you add NQ data to same folder
}


def load_databento_ohlcv(symbol: str = 'SPY', resample: str = None,
                          data_dir: Path = None) -> pd.DataFrame:
    """
    🌙 Moon Dev's Databento OHLCV Loader

    Loads 1-minute OHLCV data from Databento CSV files and optionally resamples
    to higher timeframes.

    Args:
        symbol: Trading symbol (e.g., 'SPY', 'ES', 'NQ')
        resample: Optional resample timeframe (e.g., '5min', '15min', '1H', '4H', '1D')
                  If None, returns raw 1-minute data
        data_dir: Path to databento data directory (auto-detected if None)

    Returns:
        pd.DataFrame with columns: Open, High, Low, Close, Volume
        Index: datetime
    """
    if data_dir is None:
        # Auto-detect directory based on symbol
        data_dir = DATABENTO_SYMBOL_DIRS.get(symbol, DATABENTO_DATA_DIR)

    data_dir = Path(data_dir)

    if not data_dir.exists():
        raise FileNotFoundError(f"🌙 Databento data directory not found: {data_dir}")

    # Find all CSV files
    csv_files = sorted(data_dir.glob("*.ohlcv-1m.csv"))

    if not csv_files:
        # Try compressed files
        csv_files = sorted(data_dir.glob("*.ohlcv-1m.csv.zst"))
        if csv_files:
            print(f"🌙 Found {len(csv_files)} compressed databento files - decompressing...")
            return _load_databento_compressed(csv_files, symbol, resample)

        raise FileNotFoundError(f"🌙 No databento CSV files found in {data_dir}")

    print(f"🌙 Loading {len(csv_files)} databento files for {symbol}...")

    # Load and concatenate all files
    dfs = []
    for csv_file in csv_files:
        df = pd.read_csv(csv_file)
        # Filter for requested symbol
        if 'symbol' in df.columns:
            df = df[df['symbol'] == symbol]
        if len(df) > 0:
            dfs.append(df)

    if not dfs:
        raise ValueError(f"🌙 No data found for symbol {symbol}")

    data = pd.concat(dfs, ignore_index=True)

    # Process databento format
    data = _process_databento_data(data, resample)

    print(f"🌙 Loaded {len(data)} rows for {symbol} from Databento")
    return data


def _load_databento_compressed(csv_files: list, symbol: str, resample: str) -> pd.DataFrame:
    """Load compressed databento files (supports multi-frame zstd)."""
    import zstandard as zstd
    from io import StringIO

    dfs = []
    for csv_file in csv_files:
        with open(csv_file, 'rb') as f:
            dctx = zstd.ZstdDecompressor()
            # Use streaming reader for multi-frame zstd files
            reader = dctx.stream_reader(f)
            decompressed = reader.read().decode('utf-8')

        # Parse CSV from string
        df = pd.read_csv(StringIO(decompressed))

        # Filter for requested symbol (supports partial match for futures like ES -> ESZ5)
        if 'symbol' in df.columns and len(df) > 0:
            # For futures, match the root symbol (e.g., 'ES' matches 'ESZ5', 'ESH6')
            # Exclude spreads (contain '-')
            if symbol in ['ES', 'NQ', 'GC', 'CL', 'YM', 'RTY']:
                mask = df['symbol'].str.startswith(symbol) & ~df['symbol'].str.contains('-')
                df = df[mask]
            else:
                df = df[df['symbol'] == symbol]

        if len(df) > 0:
            dfs.append(df)

    if not dfs:
        raise ValueError(f"🌙 No data found for symbol {symbol}")

    data = pd.concat(dfs, ignore_index=True)
    return _process_databento_data(data, resample)


def _process_databento_data(data: pd.DataFrame, resample: str = None) -> pd.DataFrame:
    """Process raw databento data into backtesting.py format."""
    # Databento columns: ts_event, rtype, publisher_id, instrument_id, open, high, low, close, volume, symbol

    # Parse timestamp (remove nanoseconds for pandas compatibility)
    data['datetime'] = pd.to_datetime(data['ts_event'].str.replace('Z', '', regex=False))
    data = data.set_index('datetime')
    data = data.sort_index()

    # Keep only OHLCV columns and rename to backtesting.py format
    data = data[['open', 'high', 'low', 'close', 'volume']].copy()
    data.columns = ['Open', 'High', 'Low', 'Close', 'Volume']

    # Ensure numeric types
    for col in data.columns:
        data[col] = pd.to_numeric(data[col], errors='coerce')

    # Remove duplicates (keep last)
    data = data[~data.index.duplicated(keep='last')]

    # Resample if requested
    if resample:
        data = _resample_ohlcv(data, resample)

    return data


def _resample_ohlcv(data: pd.DataFrame, timeframe: str) -> pd.DataFrame:
    """
    Resample OHLCV data to a higher timeframe.

    Args:
        data: DataFrame with Open, High, Low, Close, Volume columns
        timeframe: Target timeframe (e.g., '5min', '15min', '1H', '4H', '1D')
    """
    # Map common timeframe strings to pandas offset aliases
    tf_map = {
        '1m': '1min', '5m': '5min', '15m': '15min', '30m': '30min',
        '1h': '1H', '4h': '4H', '1d': '1D', '1w': '1W',
        '1H': '1H', '4H': '4H', '1D': '1D', '1W': '1W'
    }
    tf = tf_map.get(timeframe, timeframe)

    resampled = data.resample(tf).agg({
        'Open': 'first',
        'High': 'max',
        'Low': 'min',
        'Close': 'last',
        'Volume': 'sum'
    }).dropna()

    print(f"🌙 Resampled from 1min to {timeframe}: {len(resampled)} bars")
    return resampled


def save_combined_databento_csv(symbol: str = 'SPY', output_path: str = None,
                                 resample: str = None) -> str:
    """
    🌙 Combine all databento daily files into a single CSV for backtesting.

    Args:
        symbol: Trading symbol (default: 'SPY')
        output_path: Output file path (default: src/data/rbi/{symbol}-{timeframe}.csv)
        resample: Optional resample timeframe

    Returns:
        Path to the saved CSV file
    """
    # Load all data
    data = load_databento_ohlcv(symbol, resample=resample)

    # Determine output path
    if output_path is None:
        project_root = Path(__file__).parent.parent.parent
        tf_suffix = f"-{resample}" if resample else "-1m"
        output_path = project_root / "src" / "data" / "rbi" / f"{symbol}{tf_suffix}.csv"

    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Save with datetime column for backtesting.py
    data.reset_index().to_csv(output_path, index=False)

    print(f"🌙 Saved {len(data)} rows to {output_path}")
    return str(output_path)
