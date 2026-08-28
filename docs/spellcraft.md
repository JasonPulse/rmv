# Spellcraft calculator

```
/tools/daoc/spellcraft     pick a slot, fill its sockets, see what you get
```

Works for anyone, signed in or not. Saving a template needs an approved member
account, and each member gets five.

## The numbers are not in yet

The calculator ships with an invented dataset. Every gem, amount, imbue cost,
cap, skill requirement and overcharge chance in
`src/Rmv.Web/Tools/Spellcraft/PlaceholderSpellcraftTables.cs` was made up so the
page and the template saving could be built and demonstrated. Nothing in it came
from the game.

It is marked in three places so it cannot be mistaken for real:

1. `SpellcraftTables.Verified` is `false`.
2. Every gem and slot name carries the word "sample".
3. The page renders a warning above the form whenever `Verified` is false.

Swapping in the real tables is one file and one line. Write a class next to the
placeholder that returns a `SpellcraftTables` with `Verified = true`, then change
the single registration in `Program.cs`:

```csharp
builder.Services.AddSingleton(PlaceholderSpellcraftTables.Build());
```

Nothing in `SpellcraftCalculator`, the page, or the template store knows the
placeholder exists, so nothing else changes. The tests in
`SpellcraftCalculatorTests` use their own table and will keep passing.

## What I need from you

Below is every field the model has, its units and its range. Fill in whichever
form is easiest to get; the shapes are chosen so that a constant, a per-level
curve and a full table all fit without reshaping anything.

If a rule does not work the way the model assumes, say so rather than bending
the numbers to fit. Changing the model is cheap right now and expensive once
somebody has saved a template.

### 1. Realms

Just the three, unless spellcraft treats them differently.

| Field | Type | Notes |
|---|---|---|
| Code | short text | lower case, `a-z0-9-`, max 24 |
| Name | text | shown in the picker |

### 2. Bonuses

One row per distinct thing a gem can add. Strength and Dexterity are two rows.
Body resist and Heat resist are two rows.

| Field | Type | Range |
|---|---|---|
| Code | short text | `a-z0-9-`, max 24 |
| Name | text | as the game writes it |
| Unit | Points or Percent | decides `+26` vs `+26%` |
| Cap | integer per item level | see below |

**Cap** is the most I need spelled out. It is the largest useful amount of that
one bonus on a single item. Give it as pairs of `(item level, cap)`, one pair per
level at which the number changes, not one per level. So if the strength cap is
15 from level 20 and 26 from level 40, that is two pairs. If it never changes,
one pair.

Two questions I cannot answer myself:

1. Is the cap a property of the bonus, of the item slot, or of both? The model
   currently hangs it on the bonus. If it differs per slot, tell me and I will
   move it.
2. Does the cap count only spellcrafted bonuses, or the item's own bonuses too?
   The calculator currently only knows about gems.

### 3. Item slots

One row per place an item goes, per realm if they differ.

| Field | Type | Range |
|---|---|---|
| Code | short text | `a-z0-9-`, max 24 |
| Name | text | "Chest", "Left ring" |
| Sockets | integer | 1 to 8 |
| ImbueCapacity | decimal per item level | as pairs, same shape as Cap |
| AllowedBonusCodes | list of bonus codes | empty means every bonus |
| RealmCode | realm code or blank | blank means all three realms |

**ImbueCapacity** is the imbue points an item of that slot and level holds before
it is overcharged. Decimal, one place, because gem costs are fractional. Give it
as `(item level, capacity)` pairs.

If capacity depends on something other than slot and level, quality or condition
for instance, tell me now. That is the one field most likely to need another
input on the form, and adding it later means a migration on saved templates.

### 4. Gems

The big one. One row per gem, meaning per combination of quality and bonus that
actually exists. If a bonus has no gem at some quality, leave the row out rather
than giving it a zero.

| Field | Type | Range |
|---|---|---|
| Code | short text | `a-z0-9-`, max 24, unique |
| Name | text | the gem's name in game |
| Quality | text | the tier name, used to group the picker |
| BonusCode | bonus code | must match a row from section 2 |
| Amount | integer | > 0, in the bonus's own unit |
| ImbuePoints | decimal | >= 0, one decimal place is fine |
| SkillRequired | integer | >= 0, the spellcraft skill this gem needs |
| RealmCode | realm code or blank | blank means every realm has it |

So for N qualities across M bonuses the answer is up to N times M rows, each
carrying an Amount, an ImbuePoints and a SkillRequired as three separate
integers. A spreadsheet with those eight columns is the ideal shape. A CSV is
better than a screenshot.

### 5. Skill requirement

Given the gems in an item, what spellcraft skill does the item need? Two shapes
are already implemented, `SkillCombination.HighestGem` and
`SkillCombination.TotalOfGems`. Pick one, or describe the real rule and I will
add it. This is a `switch` in `SpellcraftCalculator`, not a formula smeared
through the totals, so a third answer is a small change.

Related, and currently not modelled at all: does the skill needed go up when an
item is overcharged? If it does, I need the rule.

### 6. Overcharge

One row per whole imbue point over the item's capacity.

| Field | Type | Range |
|---|---|---|
| PointsOver | integer | 1, 2, 3, and upward |
| SuccessPercent | integer | 0 to 100 |

Going further over than the last row means the item cannot be made, and the page
says so. So the table's last row is also the answer to "how far over can you
push it".

Three things I have assumed and would like confirmed:

1. A fraction of a point over counts as a whole point. 3.5 spent against a
   capacity of 3 is treated as one point over.
2. The chance depends only on how far over you are, not on the crafter's skill,
   not on the item, and not on a consumable.
3. There is no partial failure. It works or it does not.

If any of those is wrong the overcharge model needs another field, and that is
worth knowing before somebody trusts the number.

### 7. Item levels

| Field | Type |
|---|---|
| MinItemLevel | integer |
| MaxItemLevel | integer |

The placeholder uses 1 to 51. If spellcraft only applies above some level, say
so and the form will start there.

## How the code is arranged

Four files, none of which touch a database or the network.

`src/Rmv.Web/Tools/Spellcraft/SpellcraftTables.cs` holds the dataset types and
validates itself. A gem pointing at a bonus that does not exist throws at
startup, not on somebody's page.

`SpellcraftDesign.cs` is the untrusted side. What the form posted and what a
saved template holds are both codes that may be nonsense, so they are resolved
against the tables first. Only a `ResolvedDesign` reaches the calculator, and the
only way to get one is through `Resolve`. That is what stops a forged form
reaching the arithmetic.

`SpellcraftCalculator.cs` is the arithmetic and nothing else. Given a resolved
design it returns totals per bonus with their caps, imbue spent against capacity,
the overcharge outcome and the skill needed.

`PlaceholderSpellcraftTables.cs` is the sample set described above.

Templates live in `src/Rmv.Web/Data/`. `SpellcraftTemplate` is the row and
`SpellcraftTemplateStore` is the only thing that writes it.

## Templates

Five per member, from `SpellcraftTemplate.MaxPerMember`. That constant is the
only place the number appears: the store reads it, the page reads it, the view
reads it, and the migration's check constraint was generated from it.

The cap is enforced in the database as well as in the store. Each row carries an
`Ordinal` of 1 to 5, unique per member, with a check constraint on the range. A
count in a handler loses a race between two submits; a unique index does not.
`SpellcraftTemplateStoreTests` posts straight at the store with no page involved,
which is the same shape as a forged request, and also inserts behind the store
entirely to prove the schema refuses it.

Ownership works the way characters do. Every lookup filters on the caller's own
member id, so a template id from somebody else's page is simply not found. There
is no separate "is this yours" branch that could be forgotten, and deleting
somebody else's template gives the same answer as deleting one that does not
exist, so the page cannot be used to discover what anyone else has saved.

Saving requires `MemberPolicy.Approved`, checked in the handler against the
authorization service. Razor Pages ignores `[Authorize]` on a handler method, so
an attribute there would have looked like a guard and done nothing.

The design is stored as one encoded string, not a row per socket. The format
carries a version marker, and an unknown version fails to decode rather than
being misread, so changing the shape later is caught on read.

## What it deliberately does not do

- No item's own bonuses. It knows about gems and nothing else, so an item that
  already has strength on it will read as further from its cap than it is.
- No crafting cost, no material list, no merchant prices.
- No suggestion of what to put in the empty sockets. That is a different tool.
