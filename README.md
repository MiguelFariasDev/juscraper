# juscraper

Consulta processos e **movimentações processuais** direto nos portais públicos dos tribunais
de justiça e devolve tudo em JSON padronizado. Foi feito para alimentar o
[Iustitia](https://github.com/MiguelFariasDev/Iustitia) como alternativa ao DataJud (ver
`integrations/iustitia/`), mas funciona sozinho: biblioteca .NET, linha de comando ou API HTTP.

Escrito em **C# 14 / .NET 10 (LTS)**, a mesma plataforma do Iustitia.

```bash
dotnet run --project src/Juscraper.Cli -- consultar 0000248-42.2024.8.26.0319
```

```
0000248-42.2024.8.26.0319 · TJSP G1 · e-SAJ
  Classe        Procedimento Comum Cível
  Órgão         2ª Vara
  Situação      Extinto
  Distribuição  09/02/2024
  Movimentações: 52
    09/04/2025  Arquivado Definitivamente
    …
```

## Cobertura

| Tribunal | Sistema | Estado | Observação |
|---|---|---|---|
| TJSP | e-SAJ | **validado** | 1º e 2º grau; movimentações via AJAX paginado por cursor |
| TJAL | e-SAJ | **validado** | 1º grau com movimentações inline; 2º grau em `/cposg5` |
| TJDFT | PJe | **validado** | nova consulta pública (API REST) |
| TJCE | e-SAJ | experimental | portal respondeu; sem processo real conferido |
| TJAC | e-SAJ | experimental | parte do acervo está no Projudi, que não é coberto |
| TJMS | e-SAJ | experimental | portal não respondeu nos testes (timeout) |
| TJAM | e-SAJ | experimental | portal recusou conexões de fora do Brasil |
| TJMG, TJBA, TJPE, TJMA, TJRO | PJe | indisponível | consulta pública **antiga** (JSF), com reCAPTCHA |
| TJPB, TJAP | PJe | indisponível | firewall anti-robô da Cloudflare |
| TJES, TJRN | PJe | indisponível | **bloqueio geográfico**: recusam acessos de fora do Brasil |
| TJSC | eproc | indisponível | firewall bloqueia acesso automatizado |
| TJRS | eproc | indisponível | HTTP 403 para acesso automatizado |

*Validado*: fluxo confirmado com processo real (nov/2026) e coberto por teste de cassete.
*Experimental*: mesmo sistema de um validado; pode funcionar como está ou pedir ajuste fino.
`GET /v1/tribunais` devolve esta tabela. Os indisponíveis ficam registrados de propósito: a
API responde com o **motivo** (`CAPTCHA_EXIGIDO`, `ACESSO_BLOQUEADO`), não com um genérico.

### Mais tribunais PJe

A nova consulta pública do PJe (Angular + API REST, sem captcha) só foi encontrada no
**TJDFT**: parece uma implantação do próprio tribunal, não um padrão do CNJ. Quando outro
tribunal migrar, não é preciso nova versão:

```bash
# 1. a partir de um servidor no Brasil
dotnet run --project src/Juscraper.Cli -- detectar-pje https://<portal-de-consulta> --sigla TJXX
# 2. a linha que ele imprime vai para a configuração
Juscraper__PjeRest="TJXX=https://<api>|https://<portal>"
```

### Hospede no Brasil

TJES, TJRN e provavelmente TJAM e TJMS recusam conexões de fora do país. Rode o juscraper
numa região brasileira (Azure *Brazil South*, onde o Iustitia já está) e refaça a verificação
desses tribunais com `detectar-pje` e com os testes ao vivo.

### O que o juscraper não faz, por decisão de projeto

O juscraper **não resolve captcha, não contorna firewall e não se disfarça de navegador**.
Captcha e bloqueio de robôs são escolhas explícitas do tribunal; contorná-las viola os termos
de uso e deixa o sistema juridicamente frágil. Nesses casos o caminho é outra fonte
(provedor comercial, DataJud) ou o cadastro manual. Pelo mesmo motivo, consulta **um processo
por vez, a pedido**: não há busca por nome, por OAB nem varredura em lote.

## Como funciona

### e-SAJ (Softplan): TJSP, TJAL, TJCE, TJAC, TJMS, TJAM

1. `GET /{cpopg|cposg}/search.do` com o número unificado. Com um resultado, redireciona para
   `show.do`; com vários (comum no 2º grau), mostra uma lista; sem nenhum,
   `#mensagemRetorno` "Não existem informações disponíveis".
2. `show.do` traz dados e partes. As **movimentações** vêm em dois formatos, conforme a
   versão do e-SAJ instalada:
   - **inline** (TJAL): `tbody#tabelaTodasMovimentacoes`;
   - **sob demanda** (TJSP, e-SAJ 26.x): `carregarMovimentacoesAjax.do`, 5 por página, com
     cursor cifrado. 1º e 2º grau usam parâmetros (`processo.codigo` × `cdProcesso`) e
     marcadores de paginação diferentes.

A sessão (cookie) aberta no `show.do` é reaproveitada no AJAX; cada consulta tem a sua.

### PJe — nova consulta pública (TJDFT)

A API que o próprio front-end consome, descoberta lendo `assets/env.js` e os bundles:

```
GET {api}/v1/processos?numeroProcesso=…        → idProcesso (token opaco, muda a cada busca)
GET {api}/v1/processos/{id}/dados
GET {api}/v1/processos/{id}/poloAtivo|poloPassivo|outrosInteressados?page=N
GET {api}/v1/processos/{id}/movimentacoes?page=N   (15 por página, 0-based)
```

### Cuidados com o tribunal

- **Throttling por host** (`System.Threading.RateLimiting`): token bucket de 1 s entre
  requisições e no máximo 2 simultâneas por portal, configuráveis.
- **Retry com backoff exponencial e jitter** (pipeline Polly v8 de
  `Microsoft.Extensions.Resilience`), só para falha transitória (rede, timeout, 408, 429,
  5xx), respeitando `Retry-After`.
- **Detecção de bloqueio**: 403, página de WAF, desafio da Cloudflare, bloqueio geográfico e
  widget de captcha viram erro tipado e **não** são repetidos.
- **HybridCache**: 30 min por padrão, incluindo o "não encontrado". Também protege contra
  estouro de demanda: duas requisições simultâneas do mesmo número viram **uma** ida ao portal.
  Resultado parcial (uma instância falhou) não entra no cache.

O custo é latência: uma consulta fria do TJSP com 50 movimentações faz ~12 requisições
(~13 s); com `max_movimentacoes=10`, 2 ou 3.

## Uso

### API HTTP

```bash
cp .env.example .env        # defina Juscraper__Api__ApiKeys
dotnet run --project src/Juscraper.Api
# ou: docker build -t juscraper . && docker run -p 8080:8080 --env-file .env juscraper
```

| Endpoint | Descrição |
|---|---|
| `GET /health` | liveness (sem autenticação) |
| `GET /openapi/v1.json` | documento OpenAPI 3.1 gerado pelo ASP.NET Core |
| `GET /v1/tribunais` | cobertura e estado de cada tribunal |
| `GET /v1/processos/{numero}` | consulta completa. Query: `instancias=1,2`, `max_movimentacoes`, `sem_cache` |
| `GET /v1/processos/{numero}/movimentacoes` | só andamentos, consolidados. `desde=AAAA-MM-DD`: a paginação **para** ao passar da data |

Resposta (resumida):

```json
{
  "numeroProcesso": "0002221-74.2006.8.07.0001",
  "tribunal": "TJDFT",
  "encontrado": true,
  "processos": [{
    "numeroProcesso": "0002221-74.2006.8.07.0001",
    "tribunal": "TJDFT", "grau": "G1", "sistema": "PJe",
    "dataAjuizamento": "2018-12-12", "emSegredoDeJustica": false,
    "classe": "CUMPRIMENTO DE SENTENÇA",
    "orgaoJulgador": "1ª Vara da Fazenda Pública do DF",
    "assuntos": ["Espécies de Títulos de Crédito"],
    "movimentos": [
      {"id": "a239e72baa7b7bd6ae4c1fb18127f13f", "nome": "Disponibilizado no DJ Eletrônico em 24/11/2026",
       "dataHora": "2026-11-25T02:31:59-03:00", "complemento": "21/11/2026 15:38:46 - Decisão (Decisão)",
       "possuiDocumento": true, "instancia": "G1"}
    ],
    "movimentosCompletos": true,
    "partes": [{"nome": "…", "tipo": "Autor", "polo": "ATIVO", "advogados": [{"nome": "…", "oab": "DF12345"}]}]
  }],
  "consultadoEm": "2026-11-25T14:47:55Z",
  "avisos": []
}
```

Os nomes seguem o `ProcessoTribunal`/`MovimentoProcesso` do Iustitia. Não encontrado é `200`
com `encontrado: false`. Erros vêm em ProblemDetails com `codigo` e `transitorio` — ver
[docs/erros.md](docs/erros.md). No e-SAJ a movimentação tem só a **data** (hora 00:00 no fuso
de Brasília); no PJe, data e hora.

Cada movimentação tem um **`id` estável entre consultas** (SHA-256 de processo, instância,
data, nome e complemento, mais um ordinal para andamentos idênticos no mesmo dia): é a chave de
deduplicação do acompanhamento diário do Iustitia. Vale para listas completas e para consultas
com `desde`; é o mesmo algoritmo da primeira versão (Python), então ids antigos continuam válidos.

### Biblioteca

```csharp
services.AddJuscraper(configuration.GetSection("Juscraper"));
// …
var resultado = await consulta.ConsultarAsync("1003603-09.2025.8.26.0319");
foreach (var m in resultado.MovimentosConsolidados)   // todas as instâncias, mais recente primeiro
    Console.WriteLine($"{m.DataHora:d} {m.Instancia} {m.Nome}");
```

### CLI

```bash
dotnet run --project src/Juscraper.Cli -- consultar <numero> [--json] [--desde 2026-11-01] [--max-movimentacoes 10]
dotnet run --project src/Juscraper.Cli -- tribunais
dotnet run --project src/Juscraper.Cli -- detectar-pje <portal> [--sigla TJXX]
dotnet run --project src/Juscraper.Cli -- gravar-cassete <numero> tests/cassettes/<nome>.json [--instancias 1]
```

## Testes

```bash
dotnet test --solution Juscraper.slnx                                   # offline: reproduz cassetes dos portais reais
dotnet test --project tests/Juscraper.Tests -- --explicit only          # ao vivo, contra os portais (rede brasileira)
```

xUnit v3 sobre o Microsoft.Testing.Platform (o executor padrão do `dotnet test` no .NET 10).
Quando um portal mudar de layout, o teste ao vivo falha e o de cassete continua passando:
regrave com `gravar-cassete` e ajuste o parser.

## Dados pessoais

- CPF e CNPJ que o PJe devolve em `participante` são **descartados** na leitura (minimização, LGPD).
- Os cassetes de `tests/cassettes/` foram **pseudonimizados** pelo `gravar-cassete`: nomes de
  partes, advogados e magistrados viram `PARTE 1`, `ADVOGADO 2`…; CPF, CNPJ e OAB são mascarados.
- O serviço não grava nada em disco; o cache é em memória.

## Estrutura

```
src/Juscraper.Core/      biblioteca: NumeroCnj, modelos, Sessao (HTTP), scrapers e-SAJ/PJe, registro, ConsultaProcessual
src/Juscraper.Api/       ASP.NET Core minimal API (ProblemDetails, OpenAPI, validação, X-Api-Key)
src/Juscraper.Cli/       System.CommandLine: consultar, tribunais, detectar-pje, gravar-cassete
tests/Juscraper.Tests/   xUnit v3 + WebApplicationFactory; tests/cassettes e tests/fixtures
integrations/iustitia/   adaptador .NET para o IConsultaProcessoTribunal do Iustitia
```

## Aviso

Os dados vêm de portais de consulta pública e podem estar incompletos, atrasados ou sob
segredo de justiça. A informação do portal não substitui a intimação oficial nem a conferência
do advogado responsável.
