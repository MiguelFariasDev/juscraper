"""O que o acompanhamento diário do Iustitia depende: id estável e paginação até ``desde``."""

from datetime import date, datetime

import httpx
import pytest

from juscraper.errors import AcessoBloqueado
from juscraper.models import Movimentacao
from juscraper.scrapers.base import FUSO_BRASILIA
from juscraper.servico import atribuir_ids
from juscraper.tribunais import _pje_rest_do_ambiente

from .test_http import _sessao


def _mov(nome: str, dia: int, complemento: str | None = None) -> Movimentacao:
    return Movimentacao(nome=nome, data_hora=datetime(2026, 11, dia, tzinfo=FUSO_BRASILIA),
                        complemento=complemento, instancia="G1")


def test_id_nao_muda_quando_chegam_andamentos_novos():
    antes = [_mov("Conclusos", 10), _mov("Juntada", 9)]
    atribuir_ids("0" * 20, antes)

    depois = [_mov("Expedido Certidão", 12), _mov("Conclusos", 10), _mov("Juntada", 9)]
    atribuir_ids("0" * 20, depois)

    assert [m.id for m in depois[1:]] == [m.id for m in antes]
    assert depois[0].id not in {m.id for m in antes}


def test_andamentos_identicos_no_mesmo_dia_tem_ids_distintos_e_estaveis():
    # O e-SAJ só dá a data: duas "Expedido Certidão" no mesmo dia são legítimas.
    antes = [_mov("Expedido Certidão", 10), _mov("Juntada", 9)]
    atribuir_ids("1" * 20, antes)

    depois = [_mov("Expedido Certidão", 10), _mov("Expedido Certidão", 10), _mov("Juntada", 9)]
    atribuir_ids("1" * 20, depois)

    assert len({m.id for m in depois}) == 3
    # O que já existia continua com o mesmo id (o ordinal conta do mais antigo).
    assert depois[1].id == antes[0].id


def test_id_depende_do_processo():
    a, b = [_mov("Conclusos", 10)], [_mov("Conclusos", 10)]
    atribuir_ids("0" * 20, a)
    atribuir_ids("9" * 20, b)
    assert a[0].id != b[0].id


async def test_desde_para_a_paginacao_do_esaj(servico_com):
    servico, reprodutor = servico_com("tjsp_cpopg")

    resultado = await servico.consultar(
        "0000248-42.2024.8.26.0319", instancias=("1",), desde=date(2025, 4, 1)
    )

    (processo,) = resultado.processos
    assert processo.movimentos, "há andamento em abril/2025"
    assert all(m.data_hora.date() >= date(2025, 4, 1) for m in processo.movimentos)
    assert sum("carregarMovimentacoesAjax" in p for p in reprodutor.pedidos) < 11, "não leu o histórico todo"


async def test_ids_com_desde_batem_com_a_consulta_completa(servico_com):
    completo, _ = servico_com("tjsp_cpopg")
    janela, _ = servico_com("tjsp_cpopg")

    todos = (await completo.consultar("0000248-42.2024.8.26.0319", instancias=("1",))).processos[0].movimentos
    recentes = (
        await janela.consultar("0000248-42.2024.8.26.0319", instancias=("1",), desde=date(2025, 1, 1))
    ).processos[0].movimentos

    ids_completos = {m.id for m in todos}
    assert recentes and all(m.id in ids_completos for m in recentes)


async def test_desde_para_a_paginacao_do_pje(servico_com):
    servico, reprodutor = servico_com("tjdft_pje")

    resultado = await servico.consultar("0002221-74.2006.8.07.0001", desde=date(2026, 11, 1))

    (processo,) = resultado.processos
    assert processo.movimentos
    assert all(m.data_hora.date() >= date(2026, 11, 1) for m in processo.movimentos)
    assert sum("/movimentacoes" in p for p in reprodutor.pedidos) == 1


def test_pje_rest_por_configuracao():
    (tribunal,) = _pje_rest_do_ambiente("tjxx=https://api.tjxx.jus.br|https://consulta.tjxx.jus.br")
    assert tribunal.sigla == "TJXX"
    assert tribunal.scraper().api_url == "https://api.tjxx.jus.br"
    assert _pje_rest_do_ambiente("") == []
    with pytest.raises(ValueError):
        _pje_rest_do_ambiente("TJXX=http://inseguro")


@pytest.mark.parametrize(
    "corpo",
    [
        "<h1>403 ERROR</h1>The Amazon CloudFront distribution is configured to block access from your country.",
        "Acesso Bloqueado! ... bloqueado para usuários localizados no exterior.",
        "<html><head><title>Just a moment...</title></head></html>",
    ],
)
async def test_bloqueios_geograficos_e_de_desafio(corpo):
    async with _sessao(lambda r: httpx.Response(200, text=corpo, headers={"content-type": "text/html"})) as sessao:
        with pytest.raises(AcessoBloqueado):
            await sessao.get("https://portal.jus.br/")
