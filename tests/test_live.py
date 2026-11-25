"""Testes contra os portais reais. NÃO rodam por padrão nem em CI.

    pytest -m live

Servem para detectar mudança de layout: se falharem e os testes de cassete passarem, o
portal mudou — regrave o cassete com tools/gravar_cassete.py e ajuste o parser.
Números escolhidos entre processos públicos (sem segredo de justiça).
"""

import pytest

from juscraper.servico import Juscraper

pytestmark = pytest.mark.live


@pytest.mark.parametrize(
    ("numero", "tribunal"),
    [
        ("0000248-42.2024.8.26.0319", "TJSP"),
        ("0700482-85.2026.8.02.0349", "TJAL"),
        ("0002221-74.2006.8.07.0001", "TJDFT"),
    ],
)
async def test_portal_responde_com_movimentacoes(numero, tribunal):
    resultado = await Juscraper(cache_ttl=0).consultar(numero, max_movimentacoes=10)
    assert resultado.tribunal == tribunal
    assert resultado.encontrado
    assert resultado.processos[0].movimentos
