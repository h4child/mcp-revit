# Normas técnicas (ABNT) relevantes para projeto residencial

As NBRs são documentos **protegidos por direito autoral e vendidos pela ABNT**.
Este resource só indica **o que cada norma cobre e quando consultá-la**, em
linguagem própria. Para valores e redação oficial, consulte a norma vigente
(catálogo ABNT, ABNT Coleção ou a biblioteca técnica da sua empresa).
Quando o Código de Obras do município cita uma norma, ela passa a ser exigência
legal. Em Paracatu, o Art. 25 da LC 176/2025 cita expressamente as NBR 16636,
15575, 9077 e 9050.

| Norma | Tema | Quando consultar no projeto |
|---|---|---|
| **NBR 16636** (partes 1 a 3) | Elaboração e desenvolvimento de projetos arquitetônicos: etapas, conteúdo de cada fase, programa de necessidades | Organizar entregas (estudo preliminar, anteprojeto, projeto legal, executivo) e o conteúdo mínimo de cada prancha |
| **NBR 6492** | Representação de projetos de arquitetura | Convenções gráficas: espessuras de linha, hachuras, cotas, legenda de esquadrias, indicação de cortes e de nível |
| **NBR 15575** (partes 1 a 6) | Desempenho de edificações habitacionais: estrutura, pisos, vedações, cobertura, sistemas hidrossanitários; conforto térmico, acústico e lumínico; durabilidade | Definir paredes, esquadrias e lajes; isolamento acústico entre dormitórios e fachada (parte 4); iluminação natural; vida útil de projeto |
| **NBR 15220** (parte 3) | Zoneamento bioclimático brasileiro e diretrizes construtivas | Tamanho e sombreamento de aberturas, transmitância de paredes e cobertura. Consulte a zona da cidade na lista anexa à norma |
| **NBR 9050** | Acessibilidade a edificações, mobiliário e espaços | Vão livre de portas, áreas de giro, rampas, banheiro acessível, maçanetas, alturas de comandos |
| **NBR 9077** | Saídas de emergência em edifícios | Escadas (dimensionamento, corrimão, guarda-corpo) e rotas de fuga. Complementada pelas Instruções Técnicas do Corpo de Bombeiros do estado (em MG, CBMMG) |
| **NBR 10821** | Esquadrias para edificações: requisitos, classificação e desempenho | Especificar janelas e portas externas (estanqueidade à água, permeabilidade ao ar, resistência ao vento) conforme altura e região |
| **NBR 15930** | Portas de madeira para edificações | Dimensões comerciais de folhas e batentes, classes de uso e desempenho |
| **NBR 7199** | Vidros na construção civil | Onde o vidro de segurança (laminado ou temperado) é obrigatório: guarda-corpos, portas, vidros próximos ao piso |
| **NBR 14718** | Guarda-corpos para edificação | Altura, resistência e vãos de guarda-corpos de varandas, mezaninos e escadas |
| **NBR 5626** | Instalações prediais de água fria e quente | Shafts, reservatórios, posição de pontos hidráulicos |
| **NBR 8160** | Sistemas prediais de esgoto sanitário | Prumadas, caixas de inspeção, ventilação do esgoto: influencia o empilhamento de banheiros |
| **NBR 5410** | Instalações elétricas de baixa tensão | Quantidade mínima de tomadas e pontos por ambiente, circuitos |
| **NBR 15526** | Redes de distribuição de gás (GN/GLP) | Abrigo de gás e ventilação permanente nos ambientes com aparelhos a gás |

## Como usar com o Revit MCP
1. Leia o Código de Obras do município (`revit://normas/municipios/<slug>`). Ele
   define os mínimos legais e diz quais NBRs são obrigatórias.
2. Rode `audit_code_compliance` para conferir áreas, vãos, portas e escadas.
3. Use `revit://normas/guias/esquadrias-e-paredes` para escolher tipologias.
4. Itens de desempenho (acústica, térmica, estanqueidade) dependem de
   especificação e ensaio. Registre a solução adotada nos parâmetros de tipo
   (`Descrição`, `Comentários de tipos`) e na especificação da obra.
