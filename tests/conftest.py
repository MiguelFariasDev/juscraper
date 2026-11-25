from __future__ import annotations

import json
from collections import defaultdict, deque
from pathlib import Path

import httpx
import pytest

from juscraper.http import ConfigHttp, PoliticaHost
from juscraper.servico import Juscraper

CASSETES = Path(__file__).parent / "cassettes"
FIXTURES = Path(__file__).parent / "fixtures"


class Reprodutor(httpx.AsyncBaseTransport):
    """Reproduz um cassete gravado por ``tools/gravar_cassete.py``.

    Casa pela URL completa (método + URL com query). Requisição não gravada é falha do
    teste: significa que o scraper mudou o que pede ao tribunal.
    """

    def __init__(self, *nomes: str) -> None:
        self._respostas: dict[tuple[str, str], deque[dict]] = defaultdict(deque)
        self.pedidos: list[str] = []
        for nome in nomes:
            dados = json.loads((CASSETES / f"{nome}.json").read_text(encoding="utf-8"))
            for interacao in dados["interacoes"]:
                self._respostas[(interacao["method"], interacao["url"])].append(interacao)

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        chave = (request.method, str(request.url))
        self.pedidos.append(str(request.url))
        fila = self._respostas.get(chave)
        if not fila:
            raise AssertionError(f"Requisição fora do cassete: {chave}")
        interacao = fila[0] if len(fila) == 1 else fila.popleft()
        cabecalhos = {"content-type": interacao["content_type"] or "text/html"}
        if interacao.get("location"):
            cabecalhos["location"] = interacao["location"]
        return httpx.Response(
            interacao["status"], headers=cabecalhos, content=interacao["body"].encode("utf-8"), request=request
        )


def config_rapida() -> ConfigHttp:
    """Sem espera entre requisições nem backoff: os testes não devem dormir."""
    return ConfigHttp(tentativas=3, backoff_base=0.001, politica_padrao=PoliticaHost(0, 4))


@pytest.fixture
def servico_com():
    def fabrica(*cassetes: str) -> tuple[Juscraper, Reprodutor]:
        reprodutor = Reprodutor(*cassetes)
        return Juscraper(config_rapida(), cache_ttl=0, transport=reprodutor), reprodutor

    return fabrica
