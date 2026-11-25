"""Número único de processo no padrão CNJ (Resolução CNJ nº 65/2008).

Espelha ``CNJNumber`` e ``TribunalResolver`` do Iustitia (Legal.Domain.Shared), para que
os dois lados concordem sobre o que é um número válido e a qual tribunal ele pertence.

Formato: ``NNNNNNN-DD.AAAA.J.TR.OOOO`` — sequencial, dígitos verificadores, ano, segmento
do Judiciário, tribunal e unidade de origem.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from juscraper.errors import NumeroCnjInvalido

_NAO_DIGITOS = re.compile(r"\D")

#: UFs na ordem dos códigos TR 01..27 da Resolução 65/2008.
_UFS = (
    "AC", "AL", "AP", "AM", "BA", "CE", "DFT", "ES", "GO", "MA", "MT", "MS", "MG",
    "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SE", "SP", "TO",
)

#: Justiça Militar Estadual existe em três estados apenas (art. 125, §3º, CF).
_MILITARES_ESTADUAIS = {13: "TJMMG", 21: "TJMRS", 26: "TJMSP"}


@dataclass(frozen=True, slots=True)
class NumeroCnj:
    """Número CNJ validado. Sempre construído por :meth:`parse`."""

    digitos: str

    @classmethod
    def parse(cls, numero: str | None) -> NumeroCnj:
        """Aceita o número com ou sem máscara e confere os dígitos verificadores."""
        if not numero or not numero.strip():
            raise NumeroCnjInvalido(numero)

        digitos = _NAO_DIGITOS.sub("", numero)
        if len(digitos) != 20 or digitos[13] == "0" or not _dv_valido(digitos):
            raise NumeroCnjInvalido(numero)

        return cls(digitos)

    @classmethod
    def valido(cls, numero: str | None) -> bool:
        try:
            cls.parse(numero)
        except NumeroCnjInvalido:
            return False
        return True

    # -- partes do número -------------------------------------------------------------

    @property
    def sequencial(self) -> str:
        return self.digitos[:7]

    @property
    def dv(self) -> str:
        return self.digitos[7:9]

    @property
    def ano(self) -> str:
        return self.digitos[9:13]

    @property
    def segmento(self) -> int:
        return int(self.digitos[13])

    @property
    def tr(self) -> str:
        return self.digitos[14:16]

    @property
    def origem(self) -> str:
        return self.digitos[16:20]

    # -- formatos ---------------------------------------------------------------------

    @property
    def formatado(self) -> str:
        """Forma canônica mascarada: ``NNNNNNN-DD.AAAA.J.TR.OOOO``."""
        d = self.digitos
        return f"{d[:7]}-{d[7:9]}.{d[9:13]}.{d[13]}.{d[14:16]}.{d[16:20]}"

    @property
    def numero_digito_ano(self) -> str:
        """``NNNNNNN-DD.AAAA`` — o primeiro campo do formulário do e-SAJ."""
        return self.formatado[:15]

    @property
    def tribunal(self) -> str | None:
        """Sigla do tribunal (ex.: ``TJSP``), ou ``None`` se J.TR não aponta para nenhum."""
        return resolver_tribunal(self.segmento, self.tr)

    def __str__(self) -> str:
        return self.formatado


def _dv_valido(digitos: str) -> bool:
    """Mod 97 base 10 (ISO 7064), como definido pela Resolução 65/2008."""
    dv = int(digitos[7:9])
    sem_dv = digitos[:7] + digitos[9:] + "00"
    return 98 - int(sem_dv) % 97 == dv


def calcular_dv(sequencial: str, ano: str, segmento: int, tr: str, origem: str) -> str:
    """Calcula os dígitos verificadores — útil em testes e para montar números."""
    base = f"{sequencial:0>7}{ano}{segmento}{tr:0>2}{origem:0>4}00"
    return f"{98 - int(base) % 97:02d}"


def resolver_tribunal(segmento: int, tr: str) -> str | None:
    try:
        codigo = int(tr)
    except ValueError:
        return None

    match segmento:
        case 1:
            return "STF"
        case 2:
            return "CNJ"
        case 3:
            return "STJ"
        case 4:
            return f"TRF{codigo}" if 1 <= codigo <= 6 else None
        case 5:
            return f"TRT{codigo}" if 1 <= codigo <= 24 else None
        case 6:
            if codigo == 0:
                return "TSE"
            uf = _uf(codigo)
            return f"TRE-{uf}" if uf else None
        case 7:
            return "STM"
        case 8:
            uf = _uf(codigo)
            # O tribunal do DF é TJDFT: a sigla inclui os Territórios.
            return f"TJ{uf}" if uf else None
        case 9:
            return _MILITARES_ESTADUAIS.get(codigo)
        case _:
            return None


def _uf(codigo: int) -> str | None:
    return _UFS[codigo - 1] if 1 <= codigo <= len(_UFS) else None
