using CafePos.Core.Common;

namespace CafePosApp.Tests;

/// <summary>
/// Нормализация телефона: то, что набирают и вставляют из контактов, превращается в E.164 один раз,
/// на границе. Без libphonenumber — правила здесь ровно те, что нужны российской кофейне, и каждая
/// из них проверяется на реальной раскладке клавиатуры, а не на идеальном вводе.
/// </summary>
public class PhoneNumberTests
{
    [Theory]
    [InlineData("89161234567")]        // «8» вместо кода страны — так пишут все
    [InlineData("8 916 123 45 67")]
    [InlineData("8-916-123-45-67")]
    [InlineData("8(916)123-45-67")]
    [InlineData("8 (916) 123-45.67")]
    public void A_leading_eight_becomes_the_country_code(string raw)
    {
        Assert.Equal("+79161234567", PhoneNumber.Normalize(raw));
    }

    [Theory]
    [InlineData("9161234567")]         // «9» — начало мобильного номера, частью номера не является
    [InlineData("(916) 123-45-67")]
    [InlineData("916.123.45.67")]
    public void A_leading_nine_without_a_code_becomes_plus_seven_nine(string raw)
    {
        Assert.Equal("+79161234567", PhoneNumber.Normalize(raw));
    }

    [Fact]
    public void A_number_that_already_carries_the_country_code_is_not_given_a_second_one()
    {
        // Обратная сторона правил плана: префикс «+7» к уже написанному «79161234567» дал бы
        // «+779161234567» — 13 цифр, длина прошла бы проверку, и на фискальный чек ушёл бы номер,
        // по которому невозможно дозвониться.
        Assert.Equal("+79161234567", PhoneNumber.Normalize("79161234567"));
        Assert.Equal("+79161234567", PhoneNumber.Normalize("7 916 123 45 67"));
    }

    [Theory]
    [InlineData("+79161234567")]
    [InlineData("+7 916 123 45 67")]
    [InlineData("+7 (916) 123-45.67")]
    public void An_explicit_country_code_is_left_alone(string raw)
    {
        Assert.Equal("+79161234567", PhoneNumber.Normalize(raw));
    }

    [Fact]
    public void A_foreign_number_keeps_the_code_it_was_given()
    {
        // Число с чужим кодом не переписывается на российский: иначе номер, который диктует клиент,
        // сохранился бы другим и «свяжемся с вами» не сработало бы.
        Assert.Equal("+12025550143", PhoneNumber.Normalize("+1 202 555 0143"));
    }

    [Fact]
    public void Digits_with_no_country_code_at_all_are_read_as_a_russian_number()
    {
        // Обратная сторона: без '+' чужой код неотличим от российского, и правило плана — «цифры без +
        // → +7». Угадывать страну по длине значило бы молча переписать номер клиента.
        Assert.Equal("+712025550143", PhoneNumber.Normalize("12025550143"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("() - .")]
    public void No_phone_at_all_is_a_normal_state_and_not_an_error(string? raw)
    {
        // Пустой телефон — это «клиент не дал», а не поломка. Исключение здесь превратило бы отказ
        // дать номер в сообщение об ошибке, и вызывающему пришлось бы его ловить, чтобы ничего
        // не сделать.
        Assert.Null(PhoneNumber.Normalize(raw));
    }

    [Theory]
    [InlineData("+7916abc4567")]       // буква внутри номера
    [InlineData("8916123abc7")]
    [InlineData("+7-916-Иван-45-67")]
    [InlineData("тел: 89161234567")]    // «тел:» — не цифра, не пробел и не скобка
    public void A_character_that_does_not_belong_in_a_number_is_a_typo_not_a_number(string raw)
    {
        // Мусор в базу — это как раз тот случай, о котором план: отклонённый символ внутри номера
        // означает опечатку, а не повод сохранить опечатку.
        Assert.Null(PhoneNumber.Normalize(raw));
    }

    [Theory]
    [InlineData("++79161234567")]      // второй '+'
    [InlineData("7+9161234567")]       // '+' после цифр
    public void A_second_country_code_mark_makes_the_text_not_a_number(string raw)
    {
        Assert.Null(PhoneNumber.Normalize(raw));
    }

    [Fact]
    public void A_number_longer_than_e164_allows_is_refused()
    {
        // E.164 — максимум 15 цифр. 16 цифр не «немного длиннее», это уже другая страна или опечатка,
        // и принять такое нельзя: значение попадёт на фискальный чек.
        Assert.Null(PhoneNumber.Normalize("1234567890123456"));
        Assert.Null(PhoneNumber.Normalize("+1234567890123456"));
        Assert.Null(PhoneNumber.Normalize("8" + new string('9', 15)));
    }

    [Fact]
    public void A_number_of_exactly_fifteen_digits_is_still_accepted()
    {
        // Граница проверяется с обеих сторон: «не длиннее 15» — это про 15, а не про 14.
        Assert.Equal("+123456789012345", PhoneNumber.Normalize("+123456789012345"));
    }

    [Fact]
    public void A_ru_number_normalised_by_hand_comes_out_identical()
    {
        // Сквозная проверка формы хранения: всё, что приходит в поле, уже E.164 и уже помещается в
        // 24 символа, объявленных в EF и в миграции.
        var normalized = PhoneNumber.Normalize("8 (916) 123-45-67")!;

        Assert.Equal("+79161234567", normalized);
        Assert.Equal('+', normalized[0]);
        Assert.True(normalized.Length <= 24);
    }

    [Theory]
    [InlineData("+79161234567", true)]
    [InlineData("+12025550143", true)]
    [InlineData("+123456789012345", true)]
    [InlineData("+7916123456", false)]     // 10 цифр — на одну коротко
    [InlineData("79161234567", false)]     // без '+' это не E.164
    [InlineData("+7 916 123 45 67", false)]// не нормализованная строка
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_answers_whether_this_till_can_use_the_number(string? normalized, bool expected)
    {
        Assert.Equal(expected, PhoneNumber.IsValid(normalized));
    }

    [Fact]
    public void The_mask_keeps_the_country_code_and_the_last_two_digits()
    {
        // Форма из плана: +7, восемь закрытых цифр по 11, две последние открыты.
        Assert.Equal("+7 ••• ••• •• 42", PhoneNumber.Mask("+79161234542"));
    }

    [Fact]
    public void The_mask_hides_eight_of_eleven_digits_of_a_ru_number()
    {
        // PCI DSS 3.4.1 требует закрыть НЕ МЕНЕЕ первых шести и последних четырёх цифр, и это пол про
        // банковской карты, а не про телефон: его цифра — минимум, а не форма. Закрыто больше
        // минимума — значит и требование выполнено, и клиент всё ещё узнаёт свой номер.
        var masked = PhoneNumber.Mask("+79161234567");

        Assert.Contains("67", masked);
        Assert.DoesNotContain("916", masked);
        Assert.DoesNotContain("123", masked);
        Assert.Equal("+", masked[..1]);
        Assert.Equal("7", masked.Substring(1, 1));
    }

    [Fact]
    public void The_mask_works_on_any_length_of_number()
    {
        Assert.Equal("+1 ••• ••• •• 43", PhoneNumber.Mask("+12025550143"));
        // Число короче одиннадцати цифр маскируется той же формой, просто групп меньше.
        Assert.Equal("+7 ••• •• 45", PhoneNumber.Mask("+71234545"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("+", "+")]
    [InlineData("()", "()")]
    public void A_mask_of_something_that_is_not_a_number_comes_back_as_it_went_in(string? raw, string expected)
    {
        // Закрывать нечего: оператор, увидевший собственную пустую строку, полезущий вводить заново,
        // полезет быстрее, чем оператор, увидевший ряд символов.
        Assert.Equal(expected, PhoneNumber.Mask(raw!));
    }

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        // Нормализация должна быть идемпотентной: чек отложен и открыт позже, телефон проходит через
        // второй раз — и второй раз он уже не должен превратиться в «+7791…».
        var once = PhoneNumber.Normalize("8 916 123 45 67");
        var twice = PhoneNumber.Normalize(once);

        Assert.Equal("+79161234567", twice);
        Assert.Equal(twice, PhoneNumber.Normalize(twice));
    }
}
