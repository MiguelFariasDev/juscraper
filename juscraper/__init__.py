"""juscraper — consulta de processos e movimentações nos portais públicos dos tribunais.

Uso como biblioteca::

    import asyncio
    from juscraper import Juscraper

    resultado = asyncio.run(Juscraper().consultar("0000248-42.2024.8.26.0319"))
    for movimento in resultado.movimentos_consolidados:
        print(movimento.data_hora, movimento.nome)
"""

__version__ = "0.1.0"

from juscraper.cnj import NumeroCnj  # noqa: E402
from juscraper.models import Movimentacao, Parte, Processo, ResultadoConsulta  # noqa: E402
from juscraper.servico import Juscraper  # noqa: E402

__all__ = ["Juscraper", "Movimentacao", "NumeroCnj", "Parte", "Processo", "ResultadoConsulta", "__version__"]
