import json
from datetime import date, datetime

from juscraper.models import Polo
from juscraper.scrapers.base import FUSO_BRASILIA
from juscraper.scrapers.pje import _assuntos, parse_movimentacoes, parse_partes


async def test_tjdft_consulta_publica_rest(servico_com):
    servico, reprodutor = servico_com("tjdft_pje")

    resultado = await servico.consultar("0002221-74.2006.8.07.0001")

    (processo,) = resultado.processos
    assert processo.tribunal == "TJDFT"
    assert processo.sistema == "PJe"
    assert processo.classe == "CUMPRIMENTO DE SENTENÇA"  # sem o "(156)" da tabela
    assert processo.assuntos == ["Espécies de Títulos de Crédito"]  # folha da árvore
    assert processo.orgao_julgador == "1ª Vara da Fazenda Pública do DF"
    assert processo.data_ajuizamento == date(2018, 12, 12)

    assert len(processo.movimentos) == 170
    assert processo.movimentos_completos
    primeiro = processo.movimentos[0]
    assert primeiro.data_hora == datetime(2026, 11, 19, 17, 45, 11, tzinfo=FUSO_BRASILIA)
    assert primeiro.nome == "Recebidos os autos"

    ativos = [p for p in processo.partes if p.polo is Polo.ATIVO]
    passivos = [p for p in processo.partes if p.polo is Polo.PASSIVO]
    assert len(ativos) == 1 and len(ativos[0].advogados) == 1
    assert len(passivos) == 2, "advogado não vira parte"

    saida = json.dumps(resultado.model_dump(mode="json"))
    assert "CPF" not in saida and "CNPJ" not in saida, "documento pessoal é descartado"


def test_advogado_e_agregado_a_parte_anterior():
    partes = parse_partes(
        [
            {"nome": "EMPRESA X", "tipo": "REU", "participante": "EMPRESA X - CNPJ: 1 (REU)"},
            {"nome": "FULANO", "tipo": "ADVOGADO", "participante": "FULANO - OAB DF12345 - CPF: 2 (ADVOGADO)"},
            {"nome": "BELTRANO", "tipo": "REU", "participante": "BELTRANO (REU)"},
        ],
        Polo.PASSIVO,
    )
    assert [p.nome for p in partes] == ["EMPRESA X", "BELTRANO"]
    assert partes[0].advogados[0].oab == "DF12345"


def test_movimento_excluido_e_ignorado():
    movimentos = parse_movimentacoes(
        [
            {"movimento": "Juntada", "dataAtualizacao": "01/02/2026 10:00:00", "movimentoExcluido": True},
            {"movimento": "Conclusos", "dataAtualizacao": "02/02/2026 11:30:00", "idDocumento": "abc"},
        ]
    )
    assert [m.nome for m in movimentos] == ["Conclusos"]
    assert movimentos[0].possui_documento


def test_assunto_e_a_folha_da_arvore():
    assert _assuntos("DIREITO CIVIL (899) - Obrigações (7681) - Espécies de Títulos de Crédito (7717)") == [
        "Espécies de Títulos de Crédito"
    ]
    assert _assuntos("Indenização por Dano Moral - Cobrança (123)") == ["Indenização por Dano Moral - Cobrança"]
    assert _assuntos(None) == []
