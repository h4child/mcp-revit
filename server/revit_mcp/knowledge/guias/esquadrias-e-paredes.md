# Esquadrias e paredes — boas práticas de projeto residencial

Guia de referência para decidir **tipo, quantidade, tamanho e posição** de portas,
janelas e paredes ao modelar no Revit. Aqui estão **boas práticas de mercado**,
não lei. Os mínimos legais vêm do Código de Obras do município, por exemplo
`revit://normas/municipios/paracatu-mg`. Quando houver conflito, vale a exigência
legal mais restritiva.

## 1. Portas

### Quantas portas por ambiente
- Cada compartimento fechado tem **um acesso principal**. Uma segunda porta só entra
  quando há uma função para ela:
  - saída para varanda ou quintal;
  - ligação cozinha → área de serviço;
  - suíte → banheiro e closet;
  - garagem → serviço.
- Uma suíte normalmente tem 3 portas: corredor → quarto, quarto → banheiro e quarto → closet,
  podendo o closet ficar sem porta. Prefira passar pelo quarto para chegar ao closet e
  ao banheiro. Closet como passagem é uma opção válida, mas precisa ser decisão do cliente.
- Banheiro de uso comum abre para a circulação, nunca direto para a cozinha.
- O lavabo fica perto da área social, de preferência sem vista direta da sala.

### Tipologia por ambiente

| Ambiente | Tipologia usual | Vão usual (L × A) |
|---|---|---|
| Entrada principal | Pivotante ou de giro, madeira maciça | 1,00–1,60 × 2,40–2,80 |
| Quartos e escritório | Giro, folha única | 0,80 × 2,10 |
| Banheiros e lavabo | Giro abrindo para fora, ou de correr embutida | 0,70–0,80 × 2,10 |
| Closet | De correr, camarão ou vão sem porta | 0,80–1,20 × 2,10 |
| Cozinha e serviço | Giro ou de correr | 0,80–0,90 × 2,10 |
| Para varanda e piscina | Correr ou deslizante em vidro, do piso ao teto | 2,40–6,00 × 2,40–2,60 |
| Garagem | Portão basculante, seccional ou de correr automatizado | 2,50–3,00 por vaga × 2,20–2,50 |
| Casa de máquinas e depósito | Giro com veneziana (ventilação permanente) | 0,80–0,90 × 2,10 |

- **Medidas comerciais de folha** (madeira): 0,60 / 0,70 / 0,80 / 0,90 × 2,10 m.
  Use a medida do **vão livre** para checar mínimos legais e acessibilidade.
- **Acessibilidade (NBR 9050):** vão livre mínimo de 0,80 m. Para uma suíte acessível
  (por exemplo a de hóspedes no térreo), use porta de 0,90 m e área de giro de 1,50 m.

### Posição e sentido de abertura
- Deixe uma **boneca de 10 a 15 cm** entre o batente e a parede perpendicular, para o
  alisar e o interruptor.
- A folha abre **contra a parede**, liberando a passagem, e não deve bater em outra porta,
  em janela, em armário ou no primeiro degrau da escada.
- **Banheiro pequeno:** abrir para fora ou usar porta de correr, para socorro a quem
  estiver caído lá dentro.
- O interruptor fica do lado da maçaneta. Alinhe as portas de um mesmo corredor.
- No Revit, use `flip_instances` para inverter o lado ou a face da folha.

## 2. Janelas

### Tipologia por ambiente

| Ambiente | Tipologia usual | Peitoril usual | Observação |
|---|---|---|---|
| Sala e estar | Correr, 2 a 4 folhas, ou pano de vidro | 0,00–1,00 m | Grandes aberturas para varanda |
| Quartos | Correr com veneziana ou persiana integrada | 1,00–1,10 m | Escurecimento total |
| Cozinha | Correr ou maxim-ar sobre a bancada | 1,10–1,20 m | Peitoril acima da bancada (0,90 + backsplash) |
| Banheiro e lavabo | Maxim-ar ou basculante, vidro mini-boreal | 1,50–1,80 m | Privacidade |
| Área de serviço | Basculante, veneziana ou maxim-ar | 1,20–1,50 m | Ventilação permanente se houver gás |
| Escritório e academia | Correr ou maxim-ar | 1,00–1,10 m | |
| Escada e hall de pé-direito duplo | Pano fixo alto e basculante | variável | |

- **Janela fixa não ventila.** Ela só conta para iluminação. Para atender o vão de
  ventilação, use tipologias que abrem (correr, maxim-ar, basculante, giro).
- **Área efetiva:** o Código mede a área do vão (L × A). Na janela de correr de 2 folhas,
  a abertura real de ventilação é cerca de metade do vão. Dimensione com folga nos ambientes de permanência prolongada.
- **Verga alinhada:** padronize o topo das janelas e das portas numa mesma cota
  (por exemplo 2,10 ou 2,20 m). Assim:
  - a fachada fica organizada;
  - uma única verga e uma única linha de contramarco atendem a todas as aberturas.
- **Peitoril de segurança:** em pavimento superior, janela com peitoril abaixo de
  1,10 m precisa de guarda-corpo ou de vidro de segurança laminado na parte inferior.

### Dimensionamento rápido de janelas
Área mínima do vão = área do piso × fração exigida.

| Ambiente | Área do piso | Fração | Vão mínimo | Janela típica |
|---|---|---|---|---|
| Quarto | 16 m² | 1/6 | 2,67 m² | 2,00 × 1,40 |
| Suíte master | 32 m² | 1/6 | 5,33 m² | 2 janelas de 2,00 × 1,40, ou 1 de 4,00 × 1,40 |
| Banheiro | 4,4 m² | 1/8 | 0,55 m² | 0,80 × 0,70 (ou exaustão mecânica, onde o Código permite) |

### Orientação solar (hemisfério sul)
- **Norte:** sol de inverno desejável. Bom para sala e quartos, com beiral ou brise horizontal.
- **Oeste:** sol forte à tarde. Evite grandes vidros; use brise vertical ou vidro de controle solar.
- **Sul:** luz difusa. Bom para escritório e áreas de serviço.
- **Leste:** sol da manhã, agradável. Bom para quartos.

## 3. Paredes

| Uso | Composição usual | Espessura acabada |
|---|---|---|
| Externa | Bloco cerâmico 14 ou 19 + reboco externo e interno (ou ACM, revestimento, isolamento) | 18–25 cm |
| Interna entre ambientes | Bloco cerâmico 9 ou 14 + reboco | 13–18 cm |
| Entre dormitórios e banheiros | Bloco 14 ou drywall duplo com lã mineral | ≥ 15 cm |
| Shaft hidráulico | Drywall ou bloco, com acesso para manutenção | 10–15 cm + vão de tubulação |
| Divisa | Bloco 14 ou 19 com acabamento externo (reboco, no mínimo) | ≥ 18 cm |

- **Acústica:** isolamento entre dormitórios e banheiros conforme NBR 15575-4.
  Dormitório com parede compartilhada com banheiro: prefira bloco maciço ou drywall com lã.
- **Prumadas:** empilhe banheiros e cozinhas do superior sobre áreas molhadas do térreo,
  ou preveja **shafts** e forro rebaixado para as tubulações.
- **Estrutura:** paredes do pavimento superior sobre vazios do térreo exigem viga.
  Alinhe as paredes entre pavimentos ou combine a solução com o projeto estrutural.

### No Revit
- Defina a **linha de localização** pela face de acabamento externa nas paredes de
  fachada (`Linha de localização = Face de acabamento: exterior`). Assim a troca de
  espessura não desloca a fachada.
- Use **eixos** (`create_grid`) nos alinhamentos estruturais e fixe-os com `pin_elements`.
- Una paredes e lajes com `join_geometry`. Confira `list_model_warnings` após trocar tipos.
- Use tipos com camadas reais (`create_layered_type`) em vez de "Genérico".

## 4. Ordem recomendada de verificação
1. Nomeie os ambientes pela função e rode `audit_code_compliance`.
2. Corrija áreas e dimensões mínimas antes de mexer nas esquadrias.
3. Dimensione as janelas pela fração exigida, já com a tipologia que abre.
4. Ajuste larguras e sentidos de abertura das portas.
5. Alinhe vergas e peitoris por pavimento.
6. Revise escada (espelho, piso, patamar) e guarda-corpos.
7. Gere cortes e fachadas na escala exigida pelo município.
