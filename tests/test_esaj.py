from datetime import date

import pytest

from juscraper.errors import RespostaInesperada
from juscraper.models import Polo
from juscraper.scrapers.esaj import classificar_pagina, parse_detalhe


async def test_tjsp_1grau_pagina_todas_as_movimentacoes_por_cursor(servico_com):
    servico, reprodutor = servico_com("tjsp_cpopg")

    resultado = await servico.consultar("0000248-42.2024.8.26.0319", instancias=("1",))

    assert resultado.encontrado
    (processo,) = resultado.processos
    assert processo.tribunal == "TJSP"
    assert processo.grau == "G1"
    assert processo.sistema == "e-SAJ"
    assert processo.classe == "Procedimento Comum Cível"
    assert processo.orgao_julgador == "2ª Vara"
    assert processo.data_ajuizamento == date(2024, 2, 9)
    assert processo.situacao == "Extinto"

    # 11 páginas de 5 (a última com 2): o cursor foi seguido até o fim.
    assert len(processo.movimentos) == 52
    assert processo.movimentos_completos
    assert sum("carregarMovimentacoesAjax" in p for p in reprodutor.pedidos) == 11

    datas = [m.data_hora for m in processo.movimentos]
    assert datas == sorted(datas, reverse=True), "mais recente primeiro"
    assert processo.movimentos[0].nome == "Arquivado Definitivamente"
    assert processo.movimentos[-1].data_hora.date() == date(2024, 2, 9)
    assert all(m.instancia == "G1" for m in processo.movimentos)
    assert any(m.complemento for m in processo.movimentos)

    polos = {p.polo for p in processo.partes}
    assert polos == {Polo.ATIVO, Polo.PASSIVO}
    assert all(p.nome.startswith("PARTE") for p in processo.partes), "cassete pseudonimizado"


async def test_tjsp_1e2grau_devolve_as_duas_instancias(servico_com):
    servico, _ = servico_com("tjsp_1e2grau")

    resultado = await servico.consultar("1003603-09.2025.8.26.0319")

    graus = {p.grau: p for p in resultado.processos}
    assert set(graus) == {"G1", "G2"}

    segundo = graus["G2"]
    assert segundo.classe == "Apelação Cível"
    assert segundo.orgao_julgador == "18ª Câmara de Direito Público"
    assert segundo.situacao == "Julgado"
    assert segundo.juiz == "MAGISTRADO 2" or segundo.juiz.startswith("MAGISTRADO")
    assert segundo.movimentos_completos and len(segundo.movimentos) > 5
    assert all(m.instancia == "G2" for m in segundo.movimentos)
    # OAB vem num input oculto no 2º grau.
    assert any(a.oab for p in segundo.partes for a in p.advogados)

    consolidados = resultado.movimentos_consolidados
    assert len(consolidados) == len(graus["G1"].movimentos) + len(segundo.movimentos)
    assert consolidados[0].instancia == "G2", "o andamento mais recente está no tribunal"


async def test_tjal_layout_inline_e_2grau_inexistente(servico_com):
    servico, reprodutor = servico_com("tjal")

    resultado = await servico.consultar("0700482-85.2026.8.02.0349")

    (processo,) = resultado.processos
    assert processo.tribunal == "TJAL"
    assert len(processo.movimentos) == 19
    assert not any("carregarMovimentacoesAjax" in p for p in reprodutor.pedidos)
    assert processo.valor_da_causa == "R$ 591,34"
    assert resultado.avisos == [], "2º grau sem o processo é 'não encontrado', não falha"
    assert any("/cposg5/" in p for p in reprodutor.pedidos)


async def test_nao_encontrado_e_sucesso(servico_com):
    servico, _ = servico_com("tjsp_nao_encontrado")

    resultado = await servico.consultar("9999999-18.2024.8.26.0319")

    assert resultado.encontrado is False
    assert resultado.processos == []
    assert resultado.tribunal == "TJSP"


async def test_limite_de_movimentacoes_para_de_paginar(servico_com):
    servico, reprodutor = servico_com("tjsp_cpopg")

    resultado = await servico.consultar("0000248-42.2024.8.26.0319", instancias=("1",), max_movimentacoes=7)

    (processo,) = resultado.processos
    assert len(processo.movimentos) == 7
    assert processo.movimentos_completos is False
    assert sum("carregarMovimentacoesAjax" in p for p in reprodutor.pedidos) == 2


def test_segredo_de_justica():
    html = """
    <script>$.saj.acessoRecurso = { popupSenha: {
        mostrar: 'true' == 'true', titulo: 'Senha do processo' } };</script>
    <span id="numeroProcesso"></span>
    """
    assert classificar_pagina(html) == "segredo"
    pagina = parse_detalhe(html, tribunal="TJSP", grau="G1")
    assert pagina.processo.em_segredo_de_justica
    assert pagina.processo.movimentos == []


def test_pagina_desconhecida_vira_resposta_inesperada():
    with pytest.raises(RespostaInesperada):
        classificar_pagina("<html><title>Portal de Serviços | E-SAJ</title><body></body></html>")
