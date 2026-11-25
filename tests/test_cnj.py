import pytest

from juscraper.cnj import NumeroCnj, calcular_dv, resolver_tribunal
from juscraper.errors import NumeroCnjInvalido


@pytest.mark.parametrize(
    ("entrada", "tribunal"),
    [
        ("0000248-42.2024.8.26.0319", "TJSP"),
        ("00002484220248260319", "TJSP"),
        ("0700482-85.2026.8.02.0349", "TJAL"),
        ("0002221-74.2006.8.07.0001", "TJDFT"),
        ("1003603-09.2025.8.26.0319", "TJSP"),
    ],
)
def test_aceita_com_e_sem_mascara_e_resolve_tribunal(entrada, tribunal):
    numero = NumeroCnj.parse(entrada)
    assert numero.tribunal == tribunal
    assert numero.formatado == NumeroCnj.parse(numero.digitos).formatado
    assert len(numero.digitos) == 20


def test_partes_do_numero():
    numero = NumeroCnj.parse("0000248-42.2024.8.26.0319")
    assert numero.numero_digito_ano == "0000248-42.2024"
    assert (numero.ano, numero.segmento, numero.tr, numero.origem) == ("2024", 8, "26", "0319")


@pytest.mark.parametrize(
    "entrada",
    [
        None,
        "",
        "123",
        "0000248-43.2024.8.26.0319",  # DV errado
        "0000248-42.2024.0.26.0319",  # segmento 0 não existe
        "5018258-87.2026.8.13.0439",  # dígito trocado
    ],
)
def test_rejeita_invalidos(entrada):
    with pytest.raises(NumeroCnjInvalido):
        NumeroCnj.parse(entrada)
    assert not NumeroCnj.valido(entrada)


def test_calcular_dv_bate_com_numero_real():
    assert calcular_dv("0000248", "2024", 8, "26", "0319") == "42"


@pytest.mark.parametrize(
    ("segmento", "tr", "esperado"),
    [
        (8, "07", "TJDFT"),
        (8, "27", "TJTO"),
        (8, "28", None),
        (4, "06", "TRF6"),
        (5, "15", "TRT15"),
        (6, "00", "TSE"),
        (6, "26", "TRE-SP"),
        (9, "13", "TJMMG"),
        (9, "01", None),
        (1, "00", "STF"),
    ],
)
def test_resolver_tribunal(segmento, tr, esperado):
    assert resolver_tribunal(segmento, tr) == esperado
