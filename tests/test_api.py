import pytest
from fastapi.testclient import TestClient

from juscraper.api import Configuracao, criar_app
from juscraper.servico import Juscraper

from .conftest import Reprodutor, config_rapida


@pytest.fixture
def cliente():
    def fabrica(*cassetes: str, api_keys: str = "") -> TestClient:
        servico = Juscraper(config_rapida(), cache_ttl=60, transport=Reprodutor(*cassetes))
        return TestClient(criar_app(Configuracao(api_keys=api_keys), servico))

    return fabrica


def test_consulta_no_formato_do_iustitia(cliente):
    resposta = cliente("tjdft_pje").get("/v1/processos/0002221-74.2006.8.07.0001")

    assert resposta.status_code == 200
    corpo = resposta.json()
    assert corpo["encontrado"] is True
    processo = corpo["processos"][0]
    # Mesmos nomes do ProcessoTribunal/MovimentoProcesso do Iustitia, em camelCase.
    for campo in ("numeroProcesso", "tribunal", "grau", "dataAjuizamento", "emSegredoDeJustica",
                  "classe", "sistema", "orgaoJulgador", "assuntos", "movimentos"):
        assert campo in processo
    assert set(processo["movimentos"][0]) >= {"nome", "dataHora"}
    assert processo["movimentos"][0]["dataHora"] == "2026-11-19T17:45:11-03:00"


def test_nao_encontrado_e_200(cliente):
    resposta = cliente("tjsp_nao_encontrado").get("/v1/processos/99999991820248260319")
    assert resposta.status_code == 200
    assert resposta.json()["encontrado"] is False


def test_movimentacoes_incrementais(cliente):
    resposta = cliente("tjsp_1e2grau").get(
        "/v1/processos/1003603-09.2025.8.26.0319/movimentacoes", params={"desde": "2026-11-01"}
    )
    corpo = resposta.json()
    assert resposta.status_code == 200
    assert corpo["movimentos"], "há andamentos em novembro/2026 no 2º grau"
    assert all(m["dataHora"] >= "2026-11-01" for m in corpo["movimentos"])
    assert corpo["completas"] is True


@pytest.mark.parametrize(
    ("numero", "status", "codigo"),
    [
        ("123", 400, "NUMERO_CNJ_INVALIDO"),
        ("0001234-12.2024.8.19.0001", None, None),  # TJRJ: calculado abaixo
    ],
)
def test_erros_em_problem_details(cliente, numero, status, codigo):
    if status is None:
        from juscraper.cnj import calcular_dv

        numero = f"0001234-{calcular_dv('0001234', '2024', 8, '19', '0001')}.2024.8.19.0001"
        status, codigo = 422, "TRIBUNAL_NAO_SUPORTADO"
    resposta = cliente().get(f"/v1/processos/{numero}")
    assert resposta.status_code == status
    assert resposta.headers["content-type"].startswith("application/problem+json")
    assert resposta.json()["codigo"] == codigo


def test_tribunal_bloqueado_responde_com_motivo(cliente):
    from juscraper.cnj import calcular_dv

    numero = f"5000001-{calcular_dv('5000001', '2024', 8, '24', '0023')}.2024.8.24.0023"  # TJSC
    resposta = cliente().get(f"/v1/processos/{numero}")
    assert resposta.status_code == 503
    assert resposta.json()["codigo"] == "ACESSO_BLOQUEADO"
    assert resposta.json()["transitorio"] is False


def test_api_key(cliente):
    app = cliente("tjsp_nao_encontrado", api_keys="segredo-1,segredo-2")
    assert app.get("/v1/tribunais").status_code == 401
    assert app.get("/v1/tribunais", headers={"X-Api-Key": "errada"}).status_code == 401
    assert app.get("/v1/tribunais", headers={"X-Api-Key": "segredo-2"}).status_code == 200
    assert app.get("/health").status_code == 200, "health fica aberto para o orquestrador"
