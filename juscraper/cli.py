"""Linha de comando.

    juscraper consultar 0000248-42.2024.8.26.0319          # resumo legível
    juscraper consultar 0000248-42.2024.8.26.0319 --json   # JSON igual ao da API
    juscraper tribunais
    juscraper serve --port 8000
"""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import sys

from juscraper import tribunais
from juscraper.errors import JuscraperError
from juscraper.servico import Juscraper


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="juscraper", description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="comando", required=True)

    consultar = sub.add_parser("consultar", help="Consulta um processo pelo número CNJ.")
    consultar.add_argument("numero")
    consultar.add_argument("--instancias", default="1,2", help="Ex.: 1 | 2 | 1,2 (e-SAJ).")
    consultar.add_argument("--max-movimentacoes", type=int, default=None)
    consultar.add_argument("--json", action="store_true", help="Saída em JSON (formato da API).")
    consultar.add_argument("-v", "--verbose", action="store_true")

    sub.add_parser("tribunais", help="Lista os tribunais e o estado de cada integração.")

    serve = sub.add_parser("serve", help="Sobe a API HTTP.")
    serve.add_argument("--host", default="0.0.0.0")
    serve.add_argument("--port", type=int, default=8000)

    args = parser.parse_args(argv)

    if args.comando == "tribunais":
        for t in tribunais.TRIBUNAIS.values():
            print(f"{t.sigla:<6} {t.sistema:<6} {t.status:<13} {t.observacao or ''}")
        return 0

    if args.comando == "serve":
        import uvicorn

        uvicorn.run("juscraper.api:app", host=args.host, port=args.port)
        return 0

    logging.basicConfig(level=logging.INFO if args.verbose else logging.WARNING)
    instancias = tuple(i.strip() for i in args.instancias.split(",") if i.strip())
    try:
        resultado = asyncio.run(
            Juscraper().consultar(args.numero, instancias=instancias, max_movimentacoes=args.max_movimentacoes)
        )
    except JuscraperError as exc:
        print(f"[{exc.codigo}] {exc.mensagem}", file=sys.stderr)
        return 2 if exc.transitorio else 1

    if args.json:
        print(json.dumps(resultado.model_dump(mode="json", by_alias=True), ensure_ascii=False, indent=2))
        return 0

    if not resultado.encontrado:
        print(f"{resultado.numero_processo}: não encontrado no {resultado.tribunal}.")
    for processo in resultado.processos:
        print(f"\n{processo.numero_processo} · {processo.tribunal} {processo.grau} · {processo.sistema}")
        for rotulo, valor in (
            ("Classe", processo.classe),
            ("Assunto", ", ".join(processo.assuntos)),
            ("Órgão", processo.orgao_julgador),
            ("Juiz/Relator", processo.juiz),
            ("Situação", processo.situacao),
            ("Distribuição", processo.data_ajuizamento),
        ):
            if valor:
                print(f"  {rotulo:<13} {valor}")
        for parte in processo.partes:
            advogados = "; ".join(a.nome for a in parte.advogados)
            print(f"  {parte.tipo:<13} {parte.nome}" + (f"  (adv.: {advogados})" if advogados else ""))
        completas = "" if processo.movimentos_completos else " (lista truncada)"
        print(f"  Movimentações: {len(processo.movimentos)}{completas}")
        for movimento in processo.movimentos[:10]:
            quando = movimento.data_hora.strftime("%d/%m/%Y") if movimento.data_hora else "??/??/????"
            print(f"    {quando}  {movimento.nome}")
    for aviso in resultado.avisos:
        print(f"! {aviso}", file=sys.stderr)
    return 0
